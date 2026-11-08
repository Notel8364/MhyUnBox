using Windows.Media.Control;

var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var sessions = mgr.GetSessions();
Console.WriteLine("SessionCount=" + sessions.Count);
foreach (var s in sessions)
{
    Console.WriteLine("AUMID=[" + s.SourceAppUserModelId + "]");
    try
    {
        var p = await s.TryGetMediaPropertiesAsync();
        Console.WriteLine("  Title=[" + p.Title + "] Artist=[" + p.Artist + "]");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  props error: " + ex.Message);
    }
}
