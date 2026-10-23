param(
    [Parameter(Mandatory = $true)][string]$DirectoryPath,
    [switch]$Prepare
)
$ErrorActionPreference = 'Stop'
if ($Prepare) {
    New-Item -ItemType Directory -Path $DirectoryPath -Force | Out-Null
    $acl = Get-Acl -LiteralPath $DirectoryPath
    $everyone = [System.Security.Principal.SecurityIdentifier]::new('S-1-1-0')
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $everyone, [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $DirectoryPath -AclObject $acl
    exit 0
}
$owner = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$files = @(Get-ChildItem -LiteralPath $DirectoryPath -File -Recurse)
if ($files.Count -lt 2) { throw 'The archive probe did not create a private profile and secret reference.' }
foreach ($file in $files) {
    $acl = Get-Acl -LiteralPath $file.FullName
    if (!$acl.AreAccessRulesProtected -or !$acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Equals($owner)) {
        throw 'Profile storage did not retain protected current-user ownership.'
    }
    $allow = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and ($_.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 })
    if ($allow.Count -eq 0 -or @($allow | Where-Object { !$_.IdentityReference.Equals($owner) }).Count -gt 0) {
        throw 'Profile storage contains a broad file access rule.'
    }
}
Write-Output 'Windows archive profile creation/replacement privacy passed.'
