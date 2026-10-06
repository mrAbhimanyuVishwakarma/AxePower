using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using AxePower.Core.Services.Operations;

namespace AxePower.App.ViewModels;

/// <summary>
/// The confirmation window's state. Cancel is the default button; the action button carries the
/// action's own name and, for high risk, stays disabled until the acknowledgement is ticked.
/// Nothing is pre-ticked.
/// </summary>
public partial class ActionConfirmationViewModel : ObservableObject
{
    public ActionConfirmationViewModel(ActionConfirmation confirmation)
    {
        Confirmation = confirmation;
    }

    public ActionConfirmation Confirmation { get; }

    public string Action => Confirmation.Action;
    public string Source => Confirmation.Source;
    public IReadOnlyList<string> Affected => Confirmation.Affected;
    public bool HasAffected => Confirmation.Affected.Count > 0;
    public string AffectedHeader => Confirmation.Affected.Count == 1 ? "Affected (1)" : $"Affected ({Confirmation.Affected.Count})";
    public string Backup => Confirmation.Backup;
    public string RestartText => Confirmation.RestartText;
    public string AdministratorText => Confirmation.AdministratorText;
    public string RiskText => Confirmation.RiskText;
    public string RiskKey => Confirmation.Risk.ToString();
    public string ReversibilityText => Confirmation.ReversibilityText;
    public string ReversibilityKey => ReversibilityTextKey(Confirmation.Reversibility);
    public string ConfirmLabel => Confirmation.ConfirmLabel;
    public bool RequiresAcknowledgement => Confirmation.RequiresAcknowledgement;
    public string Acknowledgement => Confirmation.Acknowledgement;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _isAcknowledged;

    /// <summary>The action button works only when no acknowledgement is needed or it has been ticked.</summary>
    public bool CanConfirm => !RequiresAcknowledgement || IsAcknowledged;

    private static string ReversibilityTextKey(Reversibility level) => AxePower.App.ViewModels.ReversibilityText.Key(level);
}
