# Saved preferences

Each Windows database remembers items per page, gallery Fit/Fill, worker count, Quick/Deep/Custom mode, selected file-type families, AppData and root-file inclusion, modified-age years, and the reviewer label in the main action bar. These preferences are ordinary unencrypted metadata in that database. A portable backup includes them.

Controls become available after the database and preferences have loaded. New databases and converted HTML reviews start with 60 items per page, Fit, four workers and Quick. Saved scan configurations remain independent: Resume continues the recorded scan rather than replacing its type selection or age cutoff with the current controls. An invalid age entry is not saved; the last valid age is retained.

Changing controls shows **Preferences pending**, then **Preferences saving**. **Preferences committed** confirms that the database write succeeded. Rapid edits are combined into a bounded snapshot. Normal close and database switching finish the last save in the background before proceeding. Forcefully ending the process can lose changes that have not committed.

If saving fails, the controls retain your edits. Read the status beside **Retry preferences**, correct the cause and retry. A failed save stops a requested close or database switch; retry before trying again. If the database itself has stopped, close it and reopen to see its last committed state. Unknown newer preference versions use defaults without overwriting the saved preferences.

Opening a database starts on page one in To be reviewed, with filters cleared and stable first-found order. Search text, selection, note drafts, inspected media, zoom, passwords and encryption keys are not saved as preferences. Save review notes explicitly with a review action or **Details / notes → Save notes**. The action-bar reviewer label is separate from reviewer text already saved on a card.
