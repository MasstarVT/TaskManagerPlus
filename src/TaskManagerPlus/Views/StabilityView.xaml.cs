using System.Windows.Controls;
using System.Windows.Input;

namespace TaskManagerPlus.Views;

public partial class StabilityView : UserControl
{
    public StabilityView()
    {
        InitializeComponent();
    }

    /// <summary>Round 16, item 49: the multi-select CheckBox in each Dump analysis/Error report
    /// row's Expander header would otherwise also toggle the Expander itself (clicks bubble up
    /// from the CheckBox to the Expander's own header-click handler) - this stops that bubbling
    /// so checking the box just selects the row, matching how a normal list checkbox behaves.</summary>
    private void StopExpanderToggle(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
