using System.Collections;
using System.ComponentModel;

namespace TrailerGenerator.WinForms.Models;

/// <summary>
/// A generic <see cref="BindingList{T}"/> that provides sorting capabilities for WinForms controls (like DataGridView).
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public class SortableBindingList<T> : BindingList<T>
{
    private bool _isSorted;
    private PropertyDescriptor? _sortProperty;
    private ListSortDirection _sortDirection;

    public SortableBindingList() : base()
    {
    }

    public SortableBindingList(IList<T> list) : base(new List<T>(list))
    {
    }

    protected override bool SupportsSortingCore => true;

    protected override bool IsSortedCore => _isSorted;

    protected override PropertyDescriptor? SortPropertyCore => _sortProperty;

    protected override ListSortDirection SortDirectionCore => _sortDirection;

    protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection direction)
    {
        if (Items is List<T> list)
        {
            list.Sort((x, y) =>
            {
                var valX = prop.GetValue(x);
                var valY = prop.GetValue(y);

                int result;
                if (valX == null && valY == null)
                {
                    result = 0;
                }
                else if (valX == null)
                {
                    result = -1;
                }
                else if (valY == null)
                {
                    result = 1;
                }
                else if (valX is string strX && valY is string strY)
                {
                    result = string.Compare(strX, strY, StringComparison.OrdinalIgnoreCase);
                }
                else if (valX is IComparable compX)
                {
                    result = compX.CompareTo(valY);
                }
                else
                {
                    result = Comparer.Default.Compare(valX, valY);
                }

                return direction == ListSortDirection.Ascending ? result : -result;
            });

            _isSorted = true;
            _sortProperty = prop;
            _sortDirection = direction;

            OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
        }
    }

    protected override void RemoveSortCore()
    {
        _isSorted = false;
        _sortProperty = null;
    }
}
