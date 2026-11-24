using System.ComponentModel.DataAnnotations;

namespace Helpdesk.Shared.Models;

public enum ChangeLifecycleState
{
    [Display(Name = "Draft")]
    Draft = 0,

    [Display(Name = "Submitted")]
    Submitted = 1,

    [Display(Name = "Pending Approval")]
    PendingApproval = 2,

    [Display(Name = "Approved for Implementation")]
    ApprovedForImplementation = 3,

    [Display(Name = "Implementation In Progress")]
    ImplementationInProgress = 4,

    [Display(Name = "Implemented - Success")]
    ImplementedSuccess = 5,

    [Display(Name = "Implemented - Backed Out")]
    ImplementedBackedOut = 6,

    [Display(Name = "Cancelled")]
    Cancelled = 7,

    [Display(Name = "Implemented - Failed")]
    ImplementedFailed = 8
}
