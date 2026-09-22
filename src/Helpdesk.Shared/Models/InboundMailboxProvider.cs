namespace Helpdesk.Shared.Models;

public enum InboundMailboxProvider { Graph, Imap, Pop3 }
public enum MailboxAuthentication { MicrosoftApplication, Password }
public enum MailboxScope { Global, Organization }
public enum MailboxTlsMode { TlsOnConnect, StartTls }
public enum InitialMailImport { NewOnly, ExistingUnread, All }
public enum InboundReceiptOutcome { Pending, Succeeded, Ignored, NeedsReview, RetryableFailure, TerminalFailure }
public enum InboundAcknowledgmentStatus { Pending, InFlight, Succeeded, NotRequired, NeedsReview }
