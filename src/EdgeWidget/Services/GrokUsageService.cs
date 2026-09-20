namespace EdgeWidget.Services;

/// Grok Bot usage. Unlike Claude and Codex there is nothing to fetch: a personal plan exposes no usage
/// endpoint, and the consumption is billed to a Cursor account whose admin API is Teams-only. So the figures
/// are read from the "grok" block of EdgeWidget.settings.json, which the user keeps up to date by hand.
///
/// Read on every usage refresh, so editing the file shows up on the next refresh (or at once with
/// "Actualizar") without restarting the widget. No network, no credentials.
internal sealed class GrokUsageService
{
    public GrokSnapshot Read()
    {
        if (SettingsStore.Load().Grok is not GrokSettings grok) return GrokSnapshot.NotConfigured;

        var weekly = new GrokWindow(grok.WeeklyPercent, "Semanal", grok.WeeklyResetsAt);
        GrokWindow? onDemand = grok.OnDemandPercent is double percent
            ? new GrokWindow(percent, "Bajo demanda", null)
            : null;
        return new GrokSnapshot(false, weekly, onDemand, null);
    }
}
