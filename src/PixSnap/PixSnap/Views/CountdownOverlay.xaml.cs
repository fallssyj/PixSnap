using System.Windows;
using System.Windows.Threading;

namespace PixSnap.Views;

public partial class CountdownOverlay : Window
{
    private int _remaining;
    private readonly DispatcherTimer _timer;

    public CountdownOverlay(int seconds)
    {
        InitializeComponent();
        _remaining = seconds;
        CountdownText.Text = _remaining.ToString();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            Close();
            return;
        }
        CountdownText.Text = _remaining.ToString();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
