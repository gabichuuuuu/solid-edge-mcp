namespace SolidEdgeMcp.Core;

/// <summary>
/// Error accionable que se devuelve al cliente MCP. El mensaje no debe contener datos sensibles
/// ni trazas: el servidor lo transmite tal cual al modelo.
/// </summary>
public sealed class ToolException : Exception
{
    public ToolException(string code, string message, string? nextStep = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        NextStep = nextStep;
    }

    /// <summary>Código estable en snake_case (p. ej. <c>path_not_allowed</c>).</summary>
    public string Code { get; }

    /// <summary>Qué puede hacer el cliente para resolverlo.</summary>
    public string? NextStep { get; }

    /// <summary>Texto completo para el cliente: código, causa y siguiente paso.</summary>
    public string ToClientMessage() =>
        NextStep is null ? $"[{Code}] {Message}" : $"[{Code}] {Message} Next step: {NextStep}";

    public static ToolException InvalidArgument(string message, string? nextStep = null) =>
        new("invalid_argument", message, nextStep);

    public static ToolException NotFound(string message, string? nextStep = null) =>
        new("not_found", message, nextStep);
}
