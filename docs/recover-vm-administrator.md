# Recovering a VM whose administrator account stopped working

HyperHarbor signs in to each VM with an administrator account to change your password on every connect, open
administrator access, and apply profiles. For a VM that HyperHarbor installed, that account is `hhadmin` (or the name in
the machine profile), and only the host knows its password.

Before 0.3.0-beta.2, Windows expired that password 42 days after the install, and the VM then answered every request
with "The guest rejected the administrator credential". This page is for a VM that reached that point. VMs whose
account still works need nothing: the updated host turns off password expiry the next time it signs in (on a connect, or
with Check now in the VM's Administrator account… dialog).

## What still works

With a host on 0.3.0-beta.2 or later, Connect opens Remote Desktop with your last password and tells you the
administrator account was refused; the VM's row shows "Admin password rejected". Your own account in the VM is a
standard user, so it cannot repair the administrator account. What needs the repair: changing your password again,
administrator access, profiles, disk encryption, and Performance mode setup.

## Repair it

The host still holds the correct password. Windows only demands that it be changed, which needs the old one once.

1. On the host PC, open PowerShell as administrator and run this with the VM's name. It copies the stored password to
   the clipboard without showing it:

   ```powershell
   Add-Type -AssemblyName System.Security
   $vm = Get-VM -Name '<VM name>'
   $entropy = [Text.Encoding]::UTF8.GetBytes('HyperHarbor VM administrator credentials v1')
   $bytes = [IO.File]::ReadAllBytes("$env:ProgramData\HyperHarbor\vm-credentials.json.protected")
   $all = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($bytes, $entropy, 'LocalMachine')) | ConvertFrom-Json
   $admin = $all.($vm.Id.ToString()); $admin.UserName; Set-Clipboard $admin.Password
   ```

2. Open the VM in Hyper-V Manager (Connect) with View > Enhanced session turned off. Enhanced sessions sign in the way
   Remote Desktop does, which cannot change an expired password.
3. At the sign-in screen pick the administrator account ("HyperHarbor administrator"), click the password box, and
   choose Clipboard > Type clipboard text.
4. Windows says the password has expired. Set a new password of your choice.
5. In the HyperHarbor client, open the VM's menu > Administrator account…, enter that account name and the new password,
   and choose Save (tick "Stop this account's password from expiring" when the dialog offers it). HyperHarbor signs in
   with it, stores it, and turns off its expiry. Your own account is not changed.
6. Sign the administrator out of the VM, and clear the clipboard on the host (`Set-Clipboard $null`).

Type clipboard text can drop characters. If step 3 fails with "The user name or password is incorrect", repeat it; the
account locks for 10 minutes after 10 wrong passwords.

## If you cannot use the console

Set up… in the client (the VM's row or menu) accepts any administrator account of the VM, such as one of your own, and
makes it the account HyperHarbor uses. It resets the password of your `hh-` account, so browsers in the VM sign you
out once; the Administrator account… dialog does not, which is why the steps above use it.

## Why it happened

Windows gives local accounts a maximum password age of 42 days unless the account is marked "password never expires".
The answer file created `hhadmin` without that mark, and the host's one-time rotation of its password after the install
did not add it. PowerShell Direct refuses an expired password the same way as a wrong one. Restoring an older copy of
the VM does not help: the copy carries the same date. The technical record is in [history.md](history.md), entry 34.
