# Automatic Quickbase identity

Quickbase user tokens cannot authenticate most XML calls to `/db/main`. The earlier `API_GetUserInfo` request used that context and could fail with HTTP 400 despite a valid app-assigned user token. See [Quickbase authentication and secure access](https://help.quickbase.com/docs/authentication-and-secure-access).

Timekeeper now calls `POST https://api.quickbase.com/v1/formula/run` with the realm and user-token headers, `from` set to the configured Timecards table, and a fixed formula evaluating `UserToID(User())`, `UserToEmail(User())` and `UserToName(User())`. It never interpolates the supplied email or a record ID into the formula and does not create records. The current API contract uses `from` for the table; a record context is unnecessary for `User()`. References: [Run a formula](https://developer.quickbase.com/operation/runFormula), [official OpenAPI schema](https://developer.quickbase.com/qb-openapi-v3.json), and [current-user formula context](https://help.quickbase.com/docs/manage-user-access-based-on-a-users-relationship-to-app-content).

The returned ID must be a usable non-anonymous user ID and the returned email must match the email in Settings. Normal reads and submission checks also require the discovered ID to match the saved profile. A lookup of the supplied email alone would not prove token ownership and is deliberately not used. The response is bounded and parsed strictly; raw service responses and tokens are excluded from errors.

Regression checks use synthetic responses. A successful automated check does not establish that a particular user's live token, realm and app assignment are correct; they must run **Test connections** in the application.
