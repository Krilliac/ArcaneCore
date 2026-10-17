# Security policy

## Reporting a vulnerability

Please report security vulnerabilities privately through GitHub:
open the repository's **Security** tab and choose **Report a vulnerability**
(<https://github.com/Krilliac/ArcaneCore/security/advisories/new>).
The report becomes a private security advisory that only you and the maintainers can see.

Do not open a public issue, pull request or discussion for a vulnerability before it is fixed.

A useful report says:

- which component is affected (realm/auth server, world server, gateway, a tool, the Docker images, a workflow);
- the commit or branch you tested;
- how to reproduce it, and what an attacker gains (for example: a crafted client packet that crashes the world server,
  an authentication bypass, a way to read or change another account's data);
- any proof-of-concept, logs or packet captures, with real account data removed.

## What to expect

- We aim to acknowledge a report within 7 days.
- We will confirm whether we can reproduce it, agree a fix and a disclosure date with you, and keep you updated in the advisory.
- When the fix lands, the advisory is published with credit to you, unless you ask to stay anonymous.

ArcaneCore is maintained by volunteers, so these are aims, not guarantees.

## Supported versions

ArcaneCore has no tagged releases yet. Security fixes go to the `main` branch only; run a current `main` build.

## Scope

In scope: the code and configuration in this repository, including the server daemons, the tools under `tools/`, the Docker
images built from `apps/docker/Dockerfile`, `docker-compose.yml`, and the GitHub Actions workflows.

Out of scope:

- vulnerabilities in the World of Warcraft client itself;
- weaknesses of the original 1.12 protocol that the client requires (for example, the SRP6 parameters and the packet header
  cipher of that client version), unless ArcaneCore makes them worse than the protocol requires;
- deployments that ignore the documented defaults (for example, binding the database, Prometheus or Grafana port to a public
  address, or using weak database passwords);
- third-party dependencies, unless ArcaneCore uses them in a way that makes it exploitable. Report those upstream.
