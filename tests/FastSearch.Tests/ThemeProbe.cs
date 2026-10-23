using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FastSearch.Core;
using FastSearch.Settings;
using FastSearch.Themes;

internal static class ThemeProbe
{
    internal static void Run()
    {
        var entries = new List<IndexEntry> { new(5,5,".",true,null,null), new(10,5,"work",true,null,null), new(20,10,"FastSearch",true,null,null) };
        for (int i = 0; i < 300; i++) entries.Add(new((ulong)(100+i),20,$"report_{i:D3}.txt",false,1024+i*4096,DateTime.UnixEpoch.AddDays(i)));
        var index = new TestIndexSource(new("theme-test",5,1,0,IndexTable.FromEntries(entries)));
        var window = new FastSearch.MainWindow(index)
        { WindowStartupLocation=WindowStartupLocation.Manual, Left=-20000, Top=-20000, Width=1120, Height=720, ShowInTaskbar=false };
        var settings = new FastSearch.SettingsWindow(new AppSettings(), _ => { })
        { WindowStartupLocation=WindowStartupLocation.Manual, Left=-22000, Top=-22000, ShowInTaskbar=false };
        try
        {
            window.Show(); settings.Show();
            var grid = (DataGrid)window.FindName("Results");
            var search = (TextBox)window.FindName("SearchBox");
            search.Text="repo";
            var ready=Stopwatch.StartNew();
            while(window.DisplayedResultCount!=300)
            {
                if(ready.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("Theme preview search timed out");
                var frame=new DispatcherFrame();
                var timer=new DispatcherTimer(DispatcherPriority.Send){Interval=TimeSpan.FromMilliseconds(5)};
                timer.Tick+=(_,_)=>frame.Continue=false;
                timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
            }
            grid.SelectedIndex=1;
            var themeBox=(ComboBox)settings.FindName("ThemeBox");
            int dictionaries=Application.Current.Resources.MergedDictionaries.Count;
            foreach (var theme in new[] { AppTheme.LightGray, AppTheme.Charcoal, AppTheme.LightGray, AppTheme.Charcoal })
            {
                ThemeManager.Apply(theme);
                themeBox.SelectedValue=theme;
                Flush(window); Flush(settings);
                Color bg=theme==AppTheme.Charcoal?Color.FromRgb(30,35,43):Color.FromRgb(232,237,242);
                Color surface=theme==AppTheme.Charcoal?Color.FromRgb(39,46,56):Color.FromRgb(242,245,248);
                if (((SolidColorBrush)window.Background).Color!=bg || ((SolidColorBrush)settings.Background).Color!=bg ||
                    ((SolidColorBrush)grid.RowBackground).Color!=surface || ((SolidColorBrush)search.CaretBrush).Color!=((SolidColorBrush)window.Foreground).Color)
                    throw new Exception("Open windows did not update the selected theme");
                var row=(DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
                var cell=Descendant<DataGridCell>(row);
                if (cell is null || ((SolidColorBrush)cell.Background).Color!=((SolidColorBrush)Application.Current.Resources["Theme.Selection"]).Color)
                    throw new Exception("Selected cells lost their theme while focus was in another window");
                var header=Descendant<DataGridColumnHeader>(grid,h=>h.Column==grid.Columns[0])!;
                if (header.Template.FindName("PART_RightHeaderGripper",header) is not Thumb grip)
                    throw new Exception("Themed column headers lost their resize grip");
                double width=grid.Columns[0].ActualWidth;
                grip.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                grip.RaiseEvent(new DragDeltaEventArgs(24,0){RoutedEvent=Thumb.DragDeltaEvent});
                grip.RaiseEvent(new DragCompletedEventArgs(24,0,false){RoutedEvent=Thumb.DragCompletedEvent});
                Flush(window);
                if(grid.Columns[0].ActualWidth<=width)throw new Exception("Themed header resize grip did not resize its column");
                grid.Columns[0].Width=new DataGridLength(width);Flush(window);
                var scroll=Descendant<ScrollViewer>(grid)!;
                scroll.ScrollToVerticalOffset(100); Flush(window);
                if(scroll.VerticalOffset<1) throw new Exception("Themed result scrollbar did not scroll");
                scroll.ScrollToTop(); Flush(window);
                themeBox.IsDropDownOpen=true; Flush(settings);
                var item=(ComboBoxItem)themeBox.ItemContainerGenerator.ContainerFromIndex(0);
                if (((SolidColorBrush)item.Background).Color!=surface || !themeBox.IsDropDownOpen)
                    throw new Exception("Themed selection popup did not render correctly");
                Save(settings,$"artifacts/FastSearch-settings-{theme}-dropdown.png");
                themeBox.IsDropDownOpen=false; Flush(settings);
                int resultCount=grid.Items.Count;
                if(resultCount!=300 || search.Text!="repo" || grid.Columns[0].Header as string!="名前 ↑")
                    throw new Exception("Theme switch changed the query, results or sort");
                if (Application.Current.Resources.MergedDictionaries.Count!=dictionaries)
                    throw new Exception("Theme changes accumulated resource dictionaries");
                var handle=new WindowInteropHelper(window).Handle;
                if (DwmGetWindowAttribute(handle,35,out int actual,4)==0)
                {
                    var expected=((SolidColorBrush)Application.Current.Resources["Theme.Header"]).Color;
                    if(actual!=(expected.R|expected.G<<8|expected.B<<16)) throw new Exception("Native caption did not update its color");
                }
                Save(window,$"artifacts/FastSearch-{theme}-applied.png");
                Save(settings,$"artifacts/FastSearch-settings-{theme}-applied.png");
            }
            Console.WriteLine("THEMES: live light/dark changes in both windows, selected/unfocused rows, caret, dropdown, scrollbar, header resize grips, native title bar, stable resources and screenshots passed");
        }
        finally { settings.Close(); window.StopSearch(); window.Hide(); ThemeManager.Apply(AppTheme.LightGray); }
    }
    private static T? Descendant<T>(DependencyObject? node,Func<T,bool>? predicate=null) where T:DependencyObject
    {
        if(node is null)return null;
        if(node is T match && (predicate is null || predicate(match)))return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) if(Descendant<T>(VisualTreeHelper.GetChild(node,i),predicate) is {} found)return found;
        return null;
    }
    private static void Flush(Window window)
    { window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(()=>{})); window.UpdateLayout(); }
    private static void Save(Window window,string path)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream=File.Create(path);encoder.Save(stream);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out int value,int size);
}
