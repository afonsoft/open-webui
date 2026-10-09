using Microsoft.AspNetCore.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Components;

/// <summary>
/// Visuais compartilhados do modo do agente (SPEC-20261009-agent-modes-
/// plan-build RF-004): classes do chip/toggle (violeta no plan, neutro no
/// build), ícone e rótulos — usados por <see cref="AgentModeBadge"/> e
/// <see cref="AgentModeToggle"/>.
/// </summary>
public static class AgentModeVisuals
{
    /// <summary>Normaliza o modo; desconhecido → <c>build</c>.</summary>
    public static string Normalize(string? mode) => mode == "plan" ? "plan" : "build";

    /// <summary>
    /// Classes do chip/toggle por modo: violeta no plan (somente-leitura),
    /// cinza neutro no build.
    /// </summary>
    public static string ChipClass(string? mode) => Normalize(mode) == "plan"
        ? "border-violet-300 dark:border-violet-800 bg-violet-50 dark:bg-violet-950/40 text-violet-700 dark:text-violet-300"
        : "border-gray-200 dark:border-gray-750 bg-gray-50 dark:bg-gray-850 text-gray-500 dark:text-gray-400";

    /// <summary>Ícone do modo: prancheta no plan, martelo/chave no build.</summary>
    public static MarkupString Icon(string? mode) => new(Normalize(mode) == "plan"
        ? """<svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor" class="size-3.5 flex-none"><path stroke-linecap="round" stroke-linejoin="round" d="M9 12h3.75M9 15h3.75M9 18h3.75m3 .75H18a2.25 2.25 0 0 0 2.25-2.25V6.108c0-1.135-.845-2.098-1.976-2.192a48.424 48.424 0 0 0-1.123-.08m-5.801 0c-.065.21-.1.433-.1.664 0 .414.336.75.75.75h4.5a.75.75 0 0 0 .75-.75 2.25 2.25 0 0 0-.1-.664m-5.8 0A2.251 2.251 0 0 1 13.5 2.25H15c1.012 0 1.867.668 2.15 1.586m-5.8 0c-.376.023-.75.05-1.124.08C9.095 4.01 8.25 4.973 8.25 6.108V8.25m0 0H4.875c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125h9.75c.621 0 1.125-.504 1.125-1.125V9.375c0-.621-.504-1.125-1.125-1.125H8.25ZM6.75 12h.008v.008H6.75V12Zm0 3h.008v.008H6.75V15Zm0 3h.008v.008H6.75V18Z" /></svg>"""
        : """<svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor" class="size-3.5 flex-none"><path stroke-linecap="round" stroke-linejoin="round" d="M11.42 15.17 17.25 21A2.652 2.652 0 0 0 21 17.25l-5.877-5.877M11.42 15.17l2.496-3.03c.317-.384.74-.626 1.208-.766M11.42 15.17l-4.655 5.653a2.548 2.548 0 1 1-3.586-3.586l6.837-5.63m5.108-.233c.55-.164 1.163-.188 1.743-.14a4.5 4.5 0 0 0 4.486-6.336l-3.276 3.277a3.004 3.004 0 0 1-2.25-2.25l3.276-3.276a4.5 4.5 0 0 0-6.336 4.486c.091 1.076-.071 2.264-.904 2.95l-.102.085" /></svg>""");

    /// <summary>Rótulo do modo atual (chave i18n <c>chat.mode_*</c>).</summary>
    public static string Label(LocalizationService l, string? mode) =>
        l["chat.mode_" + Normalize(mode)];

    /// <summary>Title/aria do toggle: modo atual + hint do modo alvo.</summary>
    public static string ToggleTitle(LocalizationService l, string? mode)
    {
        var current = Normalize(mode);
        var target = current == "plan" ? "build" : "plan";
        return $"{l["chat.mode_agent"]} — {Label(l, current)}"
            + $" ({l["chat.mode_" + target + "_hint"]})";
    }
}
