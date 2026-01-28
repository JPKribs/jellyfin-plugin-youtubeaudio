# WORK IN PROGRESS

This plugin can sync content between 2 servers. I do not recommend using it at this point in time until I have tested everything fully and built this out more.

Documentation coming soon.

---

# Content Syncing

## Description

The Server Sync plugin enables one-way content synchronization from a source Jellyfin server to a local Jellyfin server. The plugin periodically scans the source server for media items (Movies, Episodes, Audio, Video) and downloads missing or updated content to the local server.

**Key Features:**
- Library-to-library mapping with path translation
- ETag-based change detection for reliable update detection
- Configurable approval workflow for new content and deletions
- Bandwidth throttling with time-based scheduling
- Companion file support (subtitles, etc.)
- Automatic retry for failed downloads (up to 3 attempts)

**How It Works:**
1. **Refresh Sync Table** task scans the source server and updates the local tracking database
2. **Sync Missing Content** task downloads queued items to the local server
3. Items progress through statuses: `Pending` → `Queued` → `Synced` (or `Errored`)

## Criteria

The following sections document the exact criteria and branching logic for each sync operation.

### New Item Sync

When the **Refresh Sync Table** task discovers a new item on the source server:

```
NEW ITEM DISCOVERED ON SOURCE
│
├─► Does local file already exist at the translated path?
│   │
│   ├─► YES: Does local file size match source file size?
│   │   │
│   │   ├─► YES → Status = SYNCED (no download needed)
│   │   │
│   │   └─► NO → Continue to approval check ↓
│   │
│   └─► NO → Continue to approval check ↓
│
└─► Is "Require Approval to Sync" enabled?
    │
    ├─► YES → Status = PENDING (awaits manual approval)
    │
    └─► NO → Status = QUEUED (will download automatically)
```

**Download occurs when:** Status is `QUEUED` and the **Sync Missing Content** task runs.

#### Approval Required Flow (New Items)

| Setting | Initial Status | Action Required |
|---------|---------------|-----------------|
| `RequireApprovalToSync = true` | `Pending` | User must manually approve item in UI to change status to `Queued` |
| `RequireApprovalToSync = false` | `Queued` | Downloads automatically on next sync task |

### File Replacement (Updates)

When an item already exists in the tracking database and is re-scanned:

```
EXISTING ITEM RE-SCANNED
│
├─► Is item status IGNORED?
│   └─► YES → No action (item stays ignored)
│
├─► Is item status PENDING_DELETION?
│   └─► YES → Restore item, Status = QUEUED (source has item again)
│
├─► Is item status PENDING?
│   └─► YES → Update metadata only, Status stays PENDING
│
├─► Has source changed? (ANY of these conditions)
│   │   • Source file size changed
│   │   • Source file path changed
│   │   • Source ETag changed (derived from file's DateModified)
│   │
│   ├─► YES → Status = QUEUED (will re-download)
│   │
│   └─► NO → Continue to local verification ↓
│
└─► Is "Detect Updated Files" enabled AND status is SYNCED?
    │
    ├─► YES → Check local file integrity:
    │   │
    │   ├─► Local file missing? → Status = QUEUED
    │   │
    │   ├─► Local file size ≠ source size? → Status = QUEUED
    │   │
    │   └─► Local file OK → No action
    │
    └─► NO → No action
```

**Pre-Download Validation:** Before actually downloading, the **Sync Missing Content** task performs a final check:

```
ITEM READY FOR DOWNLOAD
│
├─► Does local file exist with matching size?
│   │
│   ├─► YES → Status = SYNCED, skip download
│   │
│   └─► NO → Proceed with download
│
└─► After successful download:
    └─► Status = SYNCED, ETag preserved
```

#### Approval Required Flow (Replacements)

Replacements do **not** require additional approval. When source content changes:
- Items with status `Synced`, `Queued`, or `Errored` are automatically re-queued
- Items with status `Pending` stay pending but have their metadata updated
- Items with status `Ignored` are never modified

| Current Status | Source Changed | New Status |
|----------------|----------------|------------|
| `Synced` | Yes | `Queued` |
| `Queued` | Yes | `Queued` (metadata updated) |
| `Errored` | Yes | `Queued` (metadata updated) |
| `Pending` | Yes | `Pending` (metadata updated) |
| `Ignored` | Yes/No | `Ignored` (no change) |

### Local Deletion

When an item exists in the tracking database but is no longer found on the source server:

```
ITEM MISSING FROM SOURCE
│
├─► Is item status IGNORED or PENDING_DELETION?
│   └─► YES → No action
│
├─► Is item status NOT SYNCED? (Pending, Queued, Errored)
│   └─► YES → DELETE from tracking database only
│           (no local file to delete, source removed before download)
│
├─► Is "Delete If Missing From Source" enabled?
│   │
│   └─► NO → No action (orphaned local files remain)
│
└─► Item status is SYNCED, deletion is enabled:
    │
    └─► Is "Require Approval to Sync" enabled?
        │
        ├─► YES → Status = PENDING_DELETION (awaits manual approval)
        │
        └─► NO → Status = PENDING_DELETION (queued for auto-deletion)
```

**Note:** The actual file deletion is handled by a separate process/controller, not the sync task itself. The sync task only marks items for deletion.

#### Approval Required Flow (Deletions)

| Setting | Behavior |
|---------|----------|
| `DeleteIfMissingFromSource = false` | No deletion occurs; orphaned local files remain |
| `DeleteIfMissingFromSource = true` + `RequireApprovalToSync = true` | Status = `PendingDeletion`, user must approve in UI |
| `DeleteIfMissingFromSource = true` + `RequireApprovalToSync = false` | Status = `PendingDeletion`, deletion proceeds automatically |

### Status Reference

| Status | Description |
|--------|-------------|
| `Pending` | New item awaiting user approval to sync |
| `Queued` | Approved and waiting to be downloaded |
| `Synced` | Successfully downloaded and verified |
| `Errored` | Download failed (will retry up to 3 times) |
| `Ignored` | User has chosen to never sync this item |
| `PendingDeletion` | Item removed from source, local file may be deleted |

### Change Detection

The plugin uses multiple methods to detect changes:

1. **ETag** (Primary): Derived from the source file's `DateModified` timestamp. Most reliable indicator of actual file changes.
2. **File Size**: Compared between source and local. Detects corruption or incomplete downloads.
3. **File Path**: Detects renamed or moved files on the source server.

When any of these differ between the stored record and the current source state, the item is re-queued for download.