# Connection management

The **Connections** tab supports creating, testing, editing, and deleting local connection profiles.

## Edit a connection

1. Select a row under **Existing connections**.
2. Choose **Edit Selected**.
3. Change the connection values and choose **Save Connection Changes**.

The vendor cannot be changed while editing. To use another vendor, create a new connection instead.

The client-secret field is intentionally never populated from protected storage. When editing:

- leave the client-secret field blank to retain the currently stored secret;
- enter a value to replace the stored secret; or
- select **Remove the currently stored client secret when saving** to remove it.

Saving updates the site and credential profile in one SQLite transaction and invalidates any cached access token for the credential.

## Delete a connection

1. Select a row under **Existing connections**.
2. Choose **Delete Selected**.
3. Type `DELETE CONNECTION` in the confirmation dialog.

Deletion only removes local configuration. It never sends a request to the FHIR or OAuth server.

A connection cannot be deleted while any export job references its site. This protects durable job history and allows existing jobs to keep resolving their original configuration. Delete the jobs first only when their metadata and downloaded-file retention requirements permit it.

If the credential profile is shared by another connection or job, it is retained. Otherwise, the credential profile and its protected secret are removed with the site. A deleted built-in public-test connection is not recreated at the next application start.

## Deployment

This patch does not change the database schema and requires no new migration. Rebuild the solution and restart the application after replacing the patched files.
