using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>
/// The <c>World:GmTicketAssistant</c> configuration section: the GM ticket assistant (ArcaneCore only, no reference core has one).
/// It reads open tickets, classifies them (a model may only pick a category), answers, and runs the few evidence-gated fixes of
/// <see cref="TicketPolicy"/> for the ticket's own author. Everything else is left open for a human GM. Off by default.
/// </summary>
public sealed class GmTicketAssistantOptions
{
    public const string SectionName = "World:GmTicketAssistant";

    /// <summary>The embedded starter FAQ's name; <see cref="FaqPath"/> empty means this one.</summary>
    public const string EmbeddedFaq = "ArcaneCore.World.Gm.TicketAssistant.ticket-faq.txt";

    /// <summary>Whether the assistant handles tickets at all (default false). <c>.ticket assistant on|off</c> changes it until restart.</summary>
    public bool Enabled { get; set; }

    /// <summary>The name the assistant's answers carry ("&lt;GM&gt;Name answers your ticket: ...") and the actor of its audit lines.</summary>
    public string AssistantName { get; set; } = "Ticketbot";

    /// <summary>Seconds between passes over the open tickets (1..86400).</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Seconds a model call (classification or answer) may take before the keyword fallback or an escalation takes over (1..120).</summary>
    public int ModelTimeoutSeconds { get; set; } = 15;

    /// <summary>Path of the FAQ text the assistant answers questions from; empty uses the starter FAQ built into the server.</summary>
    public string FaqPath { get; set; } = string.Empty;

    /// <summary>The most automatic actions (fixes and answers) per account per UTC day (0..1000); past it the ticket is left to a human. 0 allows none.</summary>
    public int MaxActionsPerAccountPerDay { get; set; } = 3;

    /// <summary>Seconds before the same account gets another world-changing fix (teleport, unstuck, revive, combat reset) (0..86400).</summary>
    public int ActionCooldownSeconds { get; set; } = 600;

    /// <summary>Seconds an anomaly signal (under the map, fall without landing) counts as evidence for a ticket (1..86400).</summary>
    public int SignalWindowSeconds { get; set; } = 900;

    /// <summary>Seconds the author must not have moved before "stuck" counts as evidence without an anomaly signal (0..86400).</summary>
    public int StuckIdleSeconds { get; set; } = 120;

    /// <summary>Seconds the author must have been dead or a ghost before a graveyard resurrection (0..86400).</summary>
    public int DeadSeconds { get; set; } = 180;

    /// <summary>Seconds the author must have been "in combat" with nobody fighting them before the combat state is cleared (0..86400).</summary>
    public int StaleCombatSeconds { get; set; } = 30;

    /// <summary>Whether a model may classify tickets and draft answers (when the playerbot chat stack has a model provider). Off: keywords only, no answers drafted.</summary>
    public bool UseModel { get; set; } = true;

    /// <summary>The section bound from <paramref name="configuration"/>, every number clamped to its range.</summary>
    public static GmTicketAssistantOptions Bind(IConfiguration? configuration)
    {
        var options = new GmTicketAssistantOptions();
        configuration?.GetSection(SectionName).Bind(options);
        options.PollSeconds = Math.Clamp(options.PollSeconds, 1, 86400);
        options.ModelTimeoutSeconds = Math.Clamp(options.ModelTimeoutSeconds, 1, 120);
        options.MaxActionsPerAccountPerDay = Math.Clamp(options.MaxActionsPerAccountPerDay, 0, 1000);
        options.ActionCooldownSeconds = Math.Clamp(options.ActionCooldownSeconds, 0, 86400);
        options.SignalWindowSeconds = Math.Clamp(options.SignalWindowSeconds, 1, 86400);
        options.StuckIdleSeconds = Math.Clamp(options.StuckIdleSeconds, 0, 86400);
        options.DeadSeconds = Math.Clamp(options.DeadSeconds, 0, 86400);
        options.StaleCombatSeconds = Math.Clamp(options.StaleCombatSeconds, 0, 86400);
        if (string.IsNullOrWhiteSpace(options.AssistantName))
        {
            options.AssistantName = new GmTicketAssistantOptions().AssistantName;
        }

        return options;
    }
}
