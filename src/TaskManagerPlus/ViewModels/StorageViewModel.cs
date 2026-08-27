using System.Collections;
using System.ComponentModel;
using System.Windows.Data;
using LibreHardwareMonitor.Hardware;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Storage tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer. Also takes the shared
/// EnergyThermalsViewModel purely to re-present its already-polled Temperatures list filtered
/// down to Storage hardware (#26) - no new sensor sampling here.
/// </summary>
public sealed class StorageViewModel
{
    public PerformanceViewModel Performance { get; }

    /// <summary>Per-drive temperature readings (#26), filtered from EnergyThermalsViewModel's
    /// already-polled Temperatures collection. A fresh ListCollectionView (not
    /// CollectionViewSource.GetDefaultView, which would return the same shared default view the
    /// Energy &amp; Thermals tab's own binding uses, and setting a Filter on it would wrongly
    /// filter that tab's list too) so the two tabs' views of the same underlying collection stay
    /// independent.</summary>
    public ICollectionView DriveTemperatures { get; }

    public StorageViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        Performance = performance;

        var view = new ListCollectionView((IList)energyThermals.Temperatures)
        {
            Filter = o => o is SensorReading r && r.HardwareType == HardwareType.Storage,
        };
        DriveTemperatures = view;
    }
}
