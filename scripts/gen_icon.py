# /// script
# requires-python = ">=3.10"
# dependencies = ["websocket-client"]
# ///
"""从 appicon.svg 生成 Windows 用的多尺寸 appicon.ico。

为什么需要这个脚本：MAUI 的 resizetizer 只从 appicon.svg 生成 Android/iOS 图标，
Windows 的 exe 图标由 csproj 的 <ApplicationIcon> 指定，**必须是已存在的 .ico**
（把文件删掉直接编译失败：CSC error CS7064）。所以 .ico 是签入仓库的产物，改图和它必须同步。

为什么不用 Pillow 画图（旧版的做法）：那等于把图标**用代码重画一遍**，和 appicon.svg
变成两份真相，改一处忘一处——旧版就是这么烂掉的（脚本还在画早就废弃的 "Au" 金字，
输出路径还指向已删除的 platforms/ 目录）。现在只做"一次矢量化、多次光栅化"。

为什么用 CDP 而不是 msedge --screenshot（踩过，别再改回去）：
  --screenshot 在本机 ≥128px 时不可靠，而且**两种 headless 模式表现一致**：
    128px → 全透明空图；256px → 不透明底 + 只拍到左上角（放大裁切）
  ≤64px 反而正常。文件大小和 IHDR 尺寸都"看着对"，只有真解像素才看得出来。
  CDP 的 Emulation.setDeviceMetricsOverride + Page.captureScreenshot 是精确的
  （本仓库的 CDP 截图工具链一直用它），且一个浏览器会话就能连出全部尺寸。

用法：
  uv run scripts/gen_icon.py            # 自动装 websocket-client
  python scripts/gen_icon.py            # 已装 websocket-client 时
  python scripts/gen_icon.py --edge <msedge.exe>
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

SIZES = [16, 24, 32, 48, 64, 128, 256]

# 小尺寸笔画补偿（icon hinting）：本图标是细线稿，笔画在 16px 下不到 1 个像素，
# 直接渲染会糊成一团、T 消失（实测）。分级放大 stroke-width——
#   16px → 163%：环约 1.18px，勉强立得住（标题栏就是 16px，绕不开）
#   24px → 135%：任务栏 100% 缩放的尺寸，加大后 T 能辨认
#   ≥32px 原比例：与设计稿一致
STROKE_BOOST_BY_SIZE = {16: 1.63, 24: 1.35}

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SVG_PATH = os.path.join(REPO_ROOT, "apps", "Tama.App", "Resources", "AppIcon", "appicon.svg")
ICO_PATH = os.path.join(REPO_ROOT, "apps", "Tama.App", "Resources", "AppIcon", "appicon.ico")

EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]
DEBUG_PORT = 9333


# ---------------------------------------------------------------- Edge / CDP

def find_edge(explicit: str | None) -> str:
    if explicit:
        if not os.path.exists(explicit):
            sys.exit(f"指定的 Edge 不存在: {explicit}")
        return explicit
    for p in EDGE_CANDIDATES:
        if os.path.exists(p):
            return p
    found = shutil.which("msedge")
    if found:
        return found
    sys.exit("找不到 msedge.exe；用 --edge 指定路径")


class Cdp:
    """够用即可的 CDP 客户端：同步 websocket，一次一条命令。"""

    def __init__(self, ws_url: str):
        import websocket  # noqa: PLC0415  (延迟导入，便于给出友好报错)
        self._ws = websocket.create_connection(ws_url, suppress_origin=True, timeout=30)
        self._id = 0

    def call(self, method: str, params: dict | None = None) -> dict:
        self._id += 1
        self._ws.send(json.dumps({"id": self._id, "method": method, "params": params or {}}))
        while True:
            msg = json.loads(self._ws.recv())
            if msg.get("id") == self._id:
                if "error" in msg:
                    raise RuntimeError(f"{method} -> {msg['error']}")
                return msg.get("result", {})

    def js(self, expr: str, await_promise: bool = False):
        r = self.call("Runtime.evaluate",
                      {"expression": expr, "returnByValue": True, "awaitPromise": await_promise})
        if r.get("exceptionDetails"):
            raise RuntimeError(f"JS 异常: {json.dumps(r['exceptionDetails'])[:300]}")
        return r.get("result", {}).get("value")

    def close(self) -> None:
        try:
            self._ws.close()
        except Exception:
            pass


def wait_for_port(port: int, timeout: float = 20.0) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/version", timeout=1):
                return
        except (urllib.error.URLError, OSError):
            time.sleep(0.2)
    sys.exit(f"Edge 的调试端口 {port} 没起来")


def page_ws_url(port: int) -> str:
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=5) as r:
        tabs = json.load(r)
    for t in tabs:
        if t.get("type") == "page":
            return t["webSocketDebuggerUrl"]
    sys.exit("找不到可用的 page target")


# ---------------------------------------------------------------- 光栅化

def render_all_sizes(edge: str, workdir: str, sizes: list[int] | None = None,
                     svg_path: str | None = None) -> list[tuple[int, bytes]]:
    """一个浏览器会话里把各尺寸都截出来（每档都重设一次视口，保证按目标分辨率光栅化）。

    sizes 可覆盖：生成 .ico 时要全部尺寸，而单独看某个 SVG（例如启动屏）时只想要大的那档——
    启动屏的图形缩到 62%，16px 下本来就接近全白，硬跑全档会被空白守卫误杀。

    小尺寸会自动走"笔画补偿"变体（见 STROKE_BOOST_BY_SIZE）：本图标是细线稿，
    实测 16px 下环的笔画不到 1 像素，直接渲染糊成一团、T 完全消失。
    只放大 stroke-width、不动几何，所以大小尺寸是同一套形状，只是"笔更粗"。
    """
    sizes = sizes or SIZES
    src = svg_path or SVG_PATH
    base_name = os.path.basename(src)
    with open(src, encoding="utf-8") as f:
        svg_text = f.read()

    # 按补偿倍数分组，每个倍数写一份临时 SVG
    groups: dict[float, list[int]] = {}
    for size in sizes:
        groups.setdefault(STROKE_BOOST_BY_SIZE.get(size, 1.0), []).append(size)
    variants: dict[float, str] = {}
    for factor in groups:
        name = base_name if factor == 1.0 else f"__boost-{factor:g}.svg"
        text = svg_text if factor == 1.0 else boost_strokes(svg_text, factor)
        with open(os.path.join(workdir, name), "w", encoding="utf-8") as f:
            f.write(text)
        variants[factor] = name

    html_path = os.path.join(workdir, "preview.html")
    with open(html_path, "w", encoding="utf-8") as f:
        f.write(
            "<!DOCTYPE html><html><head><meta charset='utf-8'><style>"
            "html,body{margin:0;padding:0;background:transparent;overflow:hidden}"
            "img{display:block;width:100vw;height:100vh}"
            f"</style></head><body><img id='i' src='{base_name}'></body></html>"
        )

    proc = subprocess.Popen(
        [edge, "--headless=new", "--disable-gpu", "--hide-scrollbars",
         "--force-device-scale-factor=1",
         f"--remote-debugging-port={DEBUG_PORT}", "--remote-allow-origins=*",
         f"--user-data-dir={os.path.join(workdir, 'edge-profile')}",
         "about:blank"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    try:
        wait_for_port(DEBUG_PORT)
        cdp = Cdp(page_ws_url(DEBUG_PORT))
        try:
            cdp.call("Page.enable")
            cdp.call("Runtime.enable")
            # 透明底：比 --default-background-color 可靠
            cdp.call("Emulation.setDefaultBackgroundColorOverride",
                     {"color": {"r": 0, "g": 0, "b": 0, "a": 0}})
            cdp.call("Page.navigate", {"url": "file:///" + html_path.replace("\\", "/")})
            wait_image(cdp)

            out: list[tuple[int, bytes]] = []
            # 逐个补偿倍数换图（换 src 后要等新图解码完再截）
            for factor, group in groups.items():
                cdp.js(f"document.getElementById('i').src = '{variants[factor]}'")
                wait_image(cdp)
                for size in sorted(group):
                    cdp.call("Emulation.setDeviceMetricsOverride",
                             {"width": size, "height": size, "deviceScaleFactor": 1, "mobile": False})
                    # 等两帧，确保视口变化已经重新布局并绘制
                    cdp.js("new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))",
                           await_promise=True)
                    shot = cdp.call("Page.captureScreenshot",
                                    {"format": "png", "fromSurface": True,
                                     "captureBeyondViewport": False})
                    png = base64.b64decode(shot["data"])
                    check_not_blank(size, png)
                    out.append((size, png))
                    tag = f"  (笔画补偿 ×{factor:g})" if factor != 1.0 else ""
                    print(f"  {size:>3}x{size:<3} {len(png):>7} bytes{tag}")
            # 按尺寸升序返回（ICO 目录顺序不影响使用，但便于比对）
            return sorted(out, key=lambda item: item[0])
        finally:
            cdp.close()
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()


def wait_image(cdp: "Cdp", timeout: float = 10.0) -> None:
    """等预览页里的 <img> 真正解码完成（含换 src 之后）。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if cdp.js("document.readyState === 'complete' "
                  "&& document.images[0] && document.images[0].complete "
                  "&& document.images[0].naturalWidth > 0"):
            return
        time.sleep(0.1)
    sys.exit("预览页的图没加载完（SVG 可能没被解析）")


def boost_strokes(svg_text: str, factor: float) -> str:
    """把所有 stroke-width 按倍数放大，生成本图标的小尺寸变体。

    只动 stroke-width，不碰几何：这样大小尺寸是同一套形状，只是"笔更粗"，
    不会出现"小图标和大图标长得不一样"。
    """
    def mul(m: "re.Match[str]") -> str:
        value = float(m.group(1)) * factor
        return f'stroke-width="{round(value, 2):g}"'

    return re.sub(r'stroke-width="([0-9.]+)"', mul, svg_text)


def check_not_blank(size: int, png: bytes) -> None:
    """空图会被压到几百字节。空图标装进 .ico 后极难查（任务栏上那一档就是一片透明），
    所以宁可在生成阶段就炸掉。"""
    if len(png) < 400:
        sys.exit(f"{size}x{size} 只有 {len(png)} 字节，几乎肯定是空白；别把空图标写进 .ico")


# ---------------------------------------------------------------- ICO 打包

def pack_ico(pngs: list[tuple[int, bytes]], out_path: str) -> None:
    """把若干 (边长, PNG 字节) 打包成 ICO。

    Vista 之后 ICO 允许直接内嵌 PNG（省得自己写 BMP + AND 掩码）；
    本项目目标平台是 Win10+，全部用 PNG 条目。
    """
    count = len(pngs)
    header = struct.pack("<HHH", 0, 1, count)
    entries = b""
    data = b""
    offset = 6 + count * 16
    for size, png in pngs:
        entries += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,   # 256 在 ICO 里用 0 表示
            0 if size >= 256 else size,
            0, 0,                          # 调色板数 / 保留
            1, 32,                         # 颜色平面 / 位深
            len(png),
            offset,
        )
        data += png
        offset += len(png)

    with open(out_path, "wb") as f:
        f.write(header + entries + data)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--edge", default=None, help="msedge.exe 路径（默认自动探测）")
    args = ap.parse_args()

    try:
        import websocket  # noqa: F401
    except ImportError:
        sys.exit("缺少 websocket-client。用 `uv run scripts/gen_icon.py`，"
                 "或先 `pip install websocket-client`")

    if not os.path.exists(SVG_PATH):
        sys.exit(f"找不到源图: {SVG_PATH}")

    edge = find_edge(args.edge)
    print(f"source : {os.path.relpath(SVG_PATH, REPO_ROOT)}")
    print(f"edge   : {edge}")

    with tempfile.TemporaryDirectory() as workdir:
        # render_all_sizes 自己会把源图（以及小尺寸的加粗变体）写进 workdir
        pngs = render_all_sizes(edge, workdir)

    pack_ico(pngs, ICO_PATH)
    print(f"written: {os.path.relpath(ICO_PATH, REPO_ROOT)} ({os.path.getsize(ICO_PATH)} bytes)")


if __name__ == "__main__":
    main()
