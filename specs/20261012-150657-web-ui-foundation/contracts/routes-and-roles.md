# Contract: Routes and Roles

| Route | View | Allowed for | Platform operations |
| --- | --- | --- | --- |
| `/sign-in` | Sign-in | Signed out | `POST /api/v1/session` |
| `/set-password` | Set password with sign-in name and code | Signed out (asks to sign out first otherwise) | `POST /api/v1/credentials/redeem` |
| `/password-change` | Required password change | `passwordChangeRequired` only | `POST /api/v1/me/password` |
| `/` | Home (role-dependent shortcuts) | Signed in | `GET /api/v1/session` |
| `/account` | Own account, change password, sign out | Signed in | `GET /api/v1/me`, `POST /api/v1/me/password`, `DELETE /api/v1/session` |
| `/club` | Club details (edit) | Club Admin | `GET`/`PUT /api/v1/club` |
| `/seasons` | Seasons list, create | Club Admin | `GET`/`POST /api/v1/seasons` |
| `/seasons/:seasonId` | Season detail, activate, archive, teams list, create team | Club Admin | `GET /api/v1/seasons/{id}`, `POST …/activate`, `POST …/archive`, `GET`/`POST /api/v1/seasons/{id}/teams` |
| `/teams` | My teams | Coach or Viewer of any team; Club Admin sees all | `GET /api/v1/teams` |
| `/teams/:teamId` | Team detail (edit for Club Admin, read-only otherwise) | Club Admin; Coach or Viewer of that team | `GET`/`PUT /api/v1/teams/{id}` |
| `/members` | Members list, create | Club Admin | `GET`/`POST /api/v1/members` |
| `/members/:memberId` | Member detail: roles, credential, deactivate or reactivate, unlock, end sessions | Club Admin | `GET /api/v1/members/{id}`, `PUT`/`DELETE …/club-roles/{role}`, `PUT`/`DELETE …/team-roles/{teamId}`, `POST …/credentials`, `POST …/deactivate`, `POST …/reactivate`, `POST …/unlock`, `DELETE …/sessions` |
| any other | Not found | – | – |

## Rules

- A guard shows the not-permitted view without loading data when the
  member's roles do not allow a route (FR-012). The platform's `403` stays
  authoritative and shows the same view.
- While `passwordChangeRequired`, every route except `/password-change` and
  sign-out redirects to `/password-change` (FR-007).
- Navigation shows exactly the routes the member's roles allow (FR-011,
  SC-005). Registrars without other roles see only `/` and `/account` in this
  slice.
