using CommunityToolkit.Mvvm.ComponentModel;
using DesktopSonos.Sonos;

namespace DesktopSonos.ViewModels;

/// <summary>One visible Sonos room, plus its place in the current group topology.</summary>
public partial class SpeakerViewModel : ObservableObject
{
    private bool _suppressVolumePush;

    public SpeakerViewModel(SonosDevice device, string coordinatorUuid, int groupSize)
    {
        Device = device;
        CoordinatorUuid = coordinatorUuid;
        GroupSize = groupSize;
    }

    public SonosDevice Device { get; }
    public string Uuid => Device.Uuid;
    public string RoomName => Device.RoomName;
    public string Address => Device.Ip.ToString();
    public string Model => Device.ModelName;

    public string CoordinatorUuid { get; }
    public bool IsCoordinator => CoordinatorUuid == Uuid;
    public int GroupSize { get; }

    /// <summary>Shown under the room name in the speaker list.</summary>
    public string GroupDescription { get; set; } = "";

    /// <summary>Ticked in the UI when the user is composing a group.</summary>
    [ObservableProperty]
    private bool isChecked;

    [ObservableProperty]
    private int volume;

    [ObservableProperty]
    private bool isMuted;

    /// <summary>Updates the slider from a poll or event without echoing it back to the speaker.</summary>
    public void SetVolumeFromDevice(int value)
    {
        if (Volume == value) return;
        _suppressVolumePush = true;
        try { Volume = value; }
        finally { _suppressVolumePush = false; }
    }

    public void SetMuteFromDevice(bool value)
    {
        if (IsMuted == value) return;
        _suppressVolumePush = true;
        try { IsMuted = value; }
        finally { _suppressVolumePush = false; }
    }

    partial void OnVolumeChanged(int value)
    {
        if (_suppressVolumePush) return;
        // Fire and forget: the slider must stay responsive while dragging.
        _ = PushVolumeAsync(value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_suppressVolumePush) return;
        _ = SafeAsync(() => Device.SetMuteAsync(value));
    }

    private async Task PushVolumeAsync(int value)
    {
        await SafeAsync(() => Device.SetVolumeAsync(value)).ConfigureAwait(false);
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }
}
