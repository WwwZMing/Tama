namespace Tama.Core.Exceptions;

/// <summary>
/// 业务校验/操作失败异常（如参数缺失、状态不允许、账户不存在）。
/// 各领域 Service 一律抛本异常表达业务失败，由调用方决定呈现方式：
/// 页面 catch 后落到自己的错误提示；扩展 JSON 边界（ExtensionBridge）映射为 {"error": message}。
/// 未来如需给前端区分错误类型，可在此增加 Code/错误码字段。
/// </summary>
public class TamaException : Exception
{
    public TamaException(string message) : base(message) { }

    public TamaException(string message, Exception inner) : base(message, inner) { }
}
