# Frontend development

Vue 3, TypeScript, Pinia, Vue Router, and Ant Design Vue provide the administration interface.

Run the API on port 5106, then run `npm run dev` in this directory. The development server listens on port 5173 and proxies `/api` to the API.

- `npm run test`: run the CIDR validation suite.
- `npm run build`: type-check and emit the application into `../src/Allowlist.Api/wwwroot`.
- `npm run preview`: inspect a production build locally; API requests require the API on the same origin or a reverse proxy.

Sessions use session storage and expire when the API rejects the token. Viewer accounts can inspect entries, operations, snapshots, and IP decisions. Administrative changes, audit access, and key management require an Admin account. Complete API keys appear only in the creation dialog and are discarded when it closes.
