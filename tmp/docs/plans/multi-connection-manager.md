# Phase 2: unify Connect + Manage into one split-pane dialog

## Context

Phase 1 (shipped) added multi-profile storage (`Database` field on
`ServerConnection`, `Id`-based dedup in `ConnectionStore`) and a separate
`ManageConnectionsDialog` for CRUD. After manually testing it, real feedback:

1. **Friction**: saved `Database` isn't usable until you click Test
   Connection — `DatabaseBox` is hard-disabled until then.
2. **Truncation**: long server names get cut off at the fixed dialog width
   in `ManageConnectionsDialog`, only fixable by manually resizing.
3. **No friendly naming**: label is auto-derived `server (db)`; no way to
   give a profile a memorable name.
4. **Not actually wired up**: the "Connect" button's `ConnectionDialog` uses
   an `AutoCompleteBox` for server name, which only shows suggestions once
   you start typing (`MinimumPrefixLength` default). Saved profiles are
   technically loaded but not discoverable — nothing shows them by default.

User's proposed fix (agreed): merge the standalone `ManageConnectionsDialog`
into `ConnectionDialog` itself as a two-pane layout — saved-connections list
on the left drives the connection-detail form on the right. One dialog,
reachable identically from the "Connect" button and from the File menu, so
saved profiles are visible the instant the dialog opens.

Key implementation fact that keeps this cheap: `ConnectionDialog`'s public
contract (ctor `(ICredentialService, ConnectionStore, ServerConnection?
initial, bool startBlank)`, plus `ResultConnection`/`ResultDatabase`
properties) doesn't need to change. The 3 existing call sites
(`QuerySessionControl.axaml.cs:364`, `:406`, `MainWindow.axaml.cs:1517`)
stay untouched — they already just do `new ConnectionDialog(...)` →
`ShowDialog<bool?>()` → read `ResultConnection`/`ResultDatabase`.

## Changes

**1. `src/PlanViewer.Core/Models/ServerConnection.cs`**
Add `public bool CustomDisplayName { get; set; }` — sticky flag so a
user-set friendly name survives the next auto-relabel-on-connect.

**2. `src/PlanViewer.App/Dialogs/ConnectionDialog.axaml`**
Restructure into two columns (`Grid ColumnDefinitions="280,*"`):
- Widen/allow resize: `Width="920" Height="580" MinWidth="800"
  MinHeight="480" CanResize="True"` (was fixed 480×520, `CanResize="False"`
  — the truncation bug's root cause). This alone fixes issue #2; add
  `TextWrapping="Wrap"` on the list row's name text as a second layer of
  defense for very long server names.
- **Left column**: "New Connection" button on top, then a `ListBox` (same
  row shape already built in `ManageConnectionsDialog.axaml` — reuse the
  item template: ★ favorite toggle, `DisplayName` (wrapped), auth-type +
  last-connected subline, small inline Rename/Duplicate/Delete buttons).
- **Right column**: existing form unchanged (Server name `AutoCompleteBox`,
  Authentication, Login, Password, Trust cert, Encrypt, Test Connection,
  Database, Connect/Cancel) — just re-hosted in the right grid column
  instead of the whole window.

**3. `src/PlanViewer.App/Dialogs/ConnectionDialog.axaml.cs`**
- Bind the left `ListBox.ItemsSource` to `_savedConnections` (already
  loaded in `PopulateSavedServers`), sorted by `LastConnected` desc. Add a
  `RefreshList()` helper called after any mutation (new/rename/duplicate/
  delete/favorite/connect-save).
- `ListBox.SelectionChanged` → calls the existing `ApplySavedConnection`
  (already handles auth/login/password/trust-cert/encrypt) plus sets
  `ServerNameBox.Text`. This is the same job `ServerName_SelectionChanged`
  already does for the autocomplete path — factor both into one shared
  method.
- **Prefill-without-test fix (issue #1)**: in `ApplySavedConnection`, if
  `saved.Database` is set, immediately set `DatabaseBox.ItemsSource = new[]
  { saved.Database }`, select it, enable `DatabaseBox` and `ConnectButton`
  right away. "Test Connection" still works as before and replaces this
  with the live/full database list plus re-validates credentials.
- **"New Connection" button** (left column top) → extract existing
  blank-form logic (currently only reachable via ctor `startBlank: true`)
  into a `ClearForm()` method; button calls it directly plus deselects the
  list, so New works without reopening the dialog.
- **Rename** (issue #3) → opens a new tiny `RenameConnectionDialog` (title +
  one `TextBox` + OK/Cancel, ~350×150, same `AppButton`/theme conventions as
  every other dialog here) pre-filled with current `DisplayName`. On OK:
  `connection.DisplayName = result; connection.CustomDisplayName = true;
  _connectionStore.AddOrUpdate(connection); RefreshList();`
- **Duplicate** → clone with fresh `Guid` id, copy credential via
  `_credentialService.GetCredential`/`SaveCredential` (same logic already
  written in `ManageConnectionsDialog.Duplicate_Click` — port it), add to
  store, refresh list, select the new row so it loads into the right pane
  for immediate editing.
- **Delete** → `_connectionStore.Delete(id)` + `_credentialService
  .DeleteCredential(id)`, refresh list. No confirmation prompt — deleting a
  saved profile only loses convenience (server/auth-mode/db choice), not
  data; re-adding is trivial. (Deliberate simplification, not an oversight.)
- **Favorite toggle** → flip + `AddOrUpdate` + refresh, same as current
  `ManageConnectionsDialog.Favorite_Click`.
- **`BuildServerConnection()`**: only auto-compute `DisplayName =
  "{server} ({db})"` when the existing saved entry (matched by `_activeId`)
  has `CustomDisplayName == false` (or there is no existing entry). Carry
  `CustomDisplayName` forward from the existing entry so a rename survives
  reconnects/edits.

**4. New: `src/PlanViewer.App/Dialogs/RenameConnectionDialog.axaml` + `.axaml.cs`**
Minimal modal: `TextBox` pre-filled with current name, OK returns the new
string via a `ResultName` property (same pattern as `ConnectionDialog`'s
`Result*` properties), Cancel returns null. No validation beyond
non-empty — this is a local label, not a security boundary.

**5. Delete: `src/PlanViewer.App/Dialogs/ManageConnectionsDialog.axaml` + `.axaml.cs`**
Superseded — its list/CRUD logic moves into `ConnectionDialog`'s left pane.

**6. `src/PlanViewer.App/MainWindow.axaml.cs`**
`ManageConnections_Click` now opens `new Dialogs.ConnectionDialog
(_credentialService, _connectionStore, startBlank: true)` instead of the
deleted `ManageConnectionsDialog` — same combined UI, just entered from the
File menu without an active query session (return value ignored, this
entry point is purely for browsing/managing).

## Not doing (scope control)

- No delete-confirmation dialog (see above — deliberate).
- No change to the 3 existing `ConnectionDialog` call sites in
  `QuerySessionControl`/`MainWindow` — contract is unchanged.
- No change to `ConnectionStore`/CLI — phase 1's `Id`-based dedup and
  `Delete` method are reused as-is.

## Verification

- `dotnet build`.
- Run the app. Click "Connect" from a query tab — confirm the saved-profile
  list is immediately visible on the left (no typing needed) with both
  existing test profiles, correctly labeled, not truncated even at default
  window size.
- Click a saved row: confirm form on the right populates including Database
  pre-selected and Connect button enabled *before* clicking Test Connection.
- Rename a profile, close/reopen the dialog, confirm the custom name stuck
  (not overwritten back to the auto `server (db)` form) after a subsequent
  successful connect.
- Duplicate a profile, confirm a second independent entry appears with
  copied credentials, edit its database, save, confirm original untouched.
- Delete a profile, confirm it disappears from the list and its credential
  is gone from the OS credential store.
- File → Manage Connections still opens the same dialog for browsing.
- Confirm `QuerySessionControl` connect flow and `MainWindow`'s file-open
  connect flow both still work unchanged (no code touched there, but
  exercise them since the dialog they call changed shape).
