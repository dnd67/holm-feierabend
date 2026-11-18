using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Holm.App.Controls;
using WinNotch;

namespace Holm.App;

/// <summary>
/// Total flexitime from the BBC extranet, as a small addition to the notch.
/// The password is sent once and not stored. The session stays sealed for this Windows or Linux account.
/// Bookings are never written.
/// </summary>
public sealed partial class NotchWindow
{
    readonly BbcExtranet _bbc = new();
    readonly DispatcherTimer _gleitzeitTick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly StackPanel _gleitzeitPanel = new() { Margin = new Thickness(2, 12, 2, 0), IsVisible = false };
    readonly TextBlock _gleitzeitLeave = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _gleitzeitNeutral = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _gleitzeitState = new() { FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _idleGleitzeit = new()
    {
        FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
    };

    CancellationTokenSource? _gleitzeitLoop;
    string? _gleitzeitRefresh;
    BbcReading? _gleitzeit;
    string? _gleitzeitProblem;
    bool _gleitzeitIncludesOpen;
    bool _paintingGleitzeit;

    void BuildGleitzeit()
    {
        _gleitzeitPanel.Children.Add(_gleitzeitLeave);
        _gleitzeitPanel.Children.Add(_gleitzeitNeutral);
        _gleitzeitPanel.Children.Add(_gleitzeitState);
        _gleitzeitState.PointerPressed += (_, _) => { if (_gleitzeitRefresh == null) ShowGleitzeitLogin(); };
        _idleView.Children.Add(_idleGleitzeit);
        _gleitzeitTick.Tick += (_, _) => PaintGleitzeit();
    }

    void StartGleitzeit()
    {
        if (!_gleitzeitTick.IsEnabled) _gleitzeitTick.Start();
        _gleitzeitRefresh = LoadGleitzeitSession();
        PaintGleitzeit();
        ShowIdleStatus();
        _gleitzeitLoop?.Cancel();
        _gleitzeitLoop = null;
        if (!_settings.Gleitzeit || _gleitzeitRefresh == null) return;
        var cancel = new CancellationTokenSource();
        _gleitzeitLoop = cancel;
        _ = PollGleitzeitAsync(cancel.Token);
    }

    void StopGleitzeit()
    {
        _gleitzeitLoop?.Cancel();
        _gleitzeitLoop = null;
        _gleitzeitTick.Stop();
        PaintGleitzeit();
        ShowIdleStatus();
    }

    void SetGleitzeit(bool on)
    {
        _settings.Gleitzeit = on;
        _settings.Save();
        if (!on) StopGleitzeit();
        else
        {
            StartGleitzeit();
            if (_gleitzeitRefresh == null) ShowGleitzeitLogin();
        }
        ApplyState(animate: true);
    }

    async Task PollGleitzeitAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? refresh = _gleitzeitRefresh;
            if (refresh == null) return;
            try
            {
                var reading = await _bbc.ReadAsync(refresh, ct).ConfigureAwait(false);
                if (reading.RefreshToken != refresh) SaveGleitzeitSession(reading.RefreshToken);
                Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    bool open = reading.Today.Any(block => block.End == null);
                    if (_gleitzeit is { } previous && previous.Today.Any(block => block.End == null) && open)
                    {
                        var gap = reading.ReadAt - previous.ReadAt;
                        if (Gleitzeit.DetectIncludesOpen(previous.Saldo, reading.Saldo, gap, wasOpen: true) is { } includes)
                            _gleitzeitIncludesOpen = includes;
                    }
                    else if (!open) _gleitzeitIncludesOpen = false;
                    _gleitzeitRefresh = reading.RefreshToken;
                    _gleitzeit = reading;
                    _gleitzeitProblem = null;
                    PaintGleitzeit();
                    ShowIdleStatus();
                });
            }
            catch (BbcExtranetException ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (ex.NeedsSignIn)
                    {
                        _gleitzeitRefresh = null;
                        ForgetGleitzeitSession();
                    }
                    _gleitzeitProblem = ex.NeedsSignIn ? "Die Anmeldung ist abgelaufen. Einmal neu anmelden." : ex.Message;
                    PaintGleitzeit();
                });
            }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    void PaintGleitzeit()
    {
        if (_paintingGleitzeit) return;
        _paintingGleitzeit = true;
        bool wasIdle = _idleGleitzeit.IsVisible;
        try
        {
            bool on = _settings.Gleitzeit;
            _gleitzeitPanel.IsVisible = on;
            if (!on)
            {
                _idleGleitzeit.IsVisible = false;
                return;
            }
            if (_gleitzeitRefresh == null || _gleitzeit == null)
            {
                _gleitzeitLeave.Text = "Wenn du jetzt gehst";
                _gleitzeitNeutral.Text = "Bis neutral";
                _gleitzeitState.Text = _gleitzeitProblem ?? "Einmal anmelden. Danach bleibt die Sitzung auf diesem PC.";
                _idleGleitzeit.IsVisible = false;
                return;
            }

            var now = DateTimeOffset.Now;
            var snap = Gleitzeit.At(_gleitzeit.Saldo, _gleitzeit.Today, now.TimeOfDay, _gleitzeit.ReadAt, now, _gleitzeitIncludesOpen);
            bool running = snap.Presence == WorkPresence.Working && !snap.Stale;
            string balance = Gleitzeit.Format(snap.Balance, running);
            _gleitzeitLeave.Text = "Wenn du jetzt gehst  " + balance;
            _gleitzeitNeutral.Text = "Bis neutral  " + Gleitzeit.Format(snap.UntilNeutral, running && snap.Balance < TimeSpan.Zero);
            _gleitzeitLeave.Foreground = snap.Balance < TimeSpan.Zero ? Negative : Look.Ink;
            _gleitzeitLeave.Opacity = snap.Stale ? 0.45 : 1;
            _gleitzeitNeutral.Opacity = snap.Stale ? 0.45 : 1;
            _gleitzeitState.Text = Describe(snap, _gleitzeit.ReadAt.LocalDateTime);
            _idleGleitzeit.Text = snap.Presence == WorkPresence.OnBreak ? "Pause" : balance;
            _idleGleitzeit.Foreground = snap.Presence == WorkPresence.OnBreak ? TimerOrange : snap.Balance < TimeSpan.Zero ? Negative : Look.Ink;
            _idleGleitzeit.Opacity = snap.Stale ? 0.45 : 1;
            _idleGleitzeit.IsVisible = true;
        }
        finally
        {
            if (wasIdle != _idleGleitzeit.IsVisible && _opened) ApplyState(animate: false);
            _paintingGleitzeit = false;
        }
    }

    static string Describe(GleitzeitSnapshot snap, DateTime readLocal)
    {
        if (snap.Stale) return "Stand von " + readLocal.ToString("HH:mm") + " · wird nicht weitergezählt";
        string state = snap.Presence switch
        {
            WorkPresence.Working => "Läuft",
            WorkPresence.OnBreak => "Pause · noch " + Math.Max(1, (int)Math.Ceiling((Gleitzeit.RequiredBreak - snap.BreakTaken).TotalMinutes)) + " Min",
            _ => "Ausgestempelt",
        };
        return state + " · gelesen " + readLocal.ToString("HH:mm");
    }

    bool GleitzeitInIdle() => _settings.Gleitzeit && _idleGleitzeit.IsVisible;

    void ShowGleitzeitLogin()
    {
        var window = new GleitzeitLoginWindow();
        window.Closed += async (_, _) =>
        {
            if (!window.Accepted) return;
            string email = window.Email;
            string password = window.Password;
            window.ClearSecret();
            try
            {
                string refresh = await _bbc.LoginAsync(email, password, CancellationToken.None).ConfigureAwait(true);
                password = "";
                SaveGleitzeitSession(refresh);
                _gleitzeitRefresh = refresh;
                _gleitzeitProblem = null;
                StartGleitzeit();
            }
            catch (BbcExtranetException ex)
            {
                _gleitzeitProblem = ex.Message;
                PaintGleitzeit();
            }
            finally { password = ""; }
        };
        window.Show(this);
    }

    void SignOutGleitzeit()
    {
        _gleitzeitRefresh = null;
        _gleitzeit = null;
        _gleitzeitProblem = null;
        ForgetGleitzeitSession();
        StopGleitzeit();
        if (_settings.Gleitzeit) _gleitzeitTick.Start();
        PaintGleitzeit();
        ApplyState(animate: true);
    }

    static readonly byte[] SessionEntropy = "holm-gleitzeit-session"u8.ToArray();
    static string SessionPath => Path.Combine(Diag.Folder, "gleitzeit-session.json");
    static readonly IBrush Negative = Brush("#FFFF453A");

    static string? LoadGleitzeitSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var session = JsonSerializer.Deserialize<GleitzeitSession>(File.ReadAllText(SessionPath));
            if (string.IsNullOrWhiteSpace(session?.Sealed)) return null;
            byte[] plain = FriendsService.UnprotectKey(Convert.FromBase64String(session.Sealed), SessionEntropy);
            string refresh = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(refresh) ? null : refresh;
        }
        catch { return null; }
    }

    static void SaveGleitzeitSession(string refreshToken)
    {
        try
        {
            byte[] sealedToken = FriendsService.ProtectKey(Encoding.UTF8.GetBytes(refreshToken), SessionEntropy);
            Directory.CreateDirectory(Diag.Folder);
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(new GleitzeitSession(Convert.ToBase64String(sealedToken))));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(SessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) { Diag.Error(ex); }
    }

    static void ForgetGleitzeitSession()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); }
        catch (Exception ex) { Diag.Error(ex); }
    }

    sealed record GleitzeitSession(string Sealed);
}

sealed class GleitzeitLoginWindow : Window
{
    readonly TextBox _email;
    readonly TextBox _password;
    public string Email => _email.Text?.Trim() ?? "";
    public string Password => _password.Text ?? "";
    public bool Accepted { get; private set; }

    public void ClearSecret() => _password.Text = "";

    public GleitzeitLoginWindow()
    {
        Title = "BBC Extranet";
        Width = 380;
        Height = 280;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.NotchFill();
        _email = HubTheme.Field("name@bbcag.ch");
        _password = HubTheme.Field("Passwort");
        _password.PasswordChar = '•';
        var message = new TextBlock
        {
            Text = "Einmal anmelden. Das Passwort geht nur ans Extranet und wird nicht gespeichert. Danach bleibt die Sitzung auf diesem PC.",
            Foreground = Look.InkA(0xA6),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var button = new Button
        {
            Content = "Anmelden",
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 120,
            Background = Look.Accent,
            Foreground = Brushes.White,
        };
        button.Click += (_, _) =>
        {
            if (Email.Length == 0 || Password.Length == 0) return;
            Accepted = true;
            Close();
        };
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        stack.Children.Add(message);
        stack.Children.Add(_email);
        stack.Children.Add(_password);
        stack.Children.Add(button);
        Content = stack;
    }
}
