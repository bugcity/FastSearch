using System.Reflection;
using System.Windows.Controls;

internal static class TestInput
{
    public static DataGridSortingEventArgs RaiseHeaderSort(DataGrid grid, DataGridColumn column)
    {
        var args = new DataGridSortingEventArgs(column);
        // Sorting is a CLR event; enter through DataGrid's protected event raiser.
        typeof(DataGrid).GetMethod("OnSorting", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(grid, [args]);
        return args;
    }
}
