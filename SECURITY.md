# Security Policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report privately through GitHub's
[private vulnerability reporting](https://github.com/chrislittle/ghcp-credit-visibility-azure/security/advisories/new)
— the **Report a vulnerability** button on this repository's Security tab. Reports are
acknowledged within a few days, and you'll be kept updated as the issue is investigated.

Useful things to include: what an attacker gains, the affected file or deployment setting,
and the conditions required (authentication, a non-default configuration, victim
interaction). A proof of concept helps but isn't required.

## Supported versions

This is a reference deployment template rather than a versioned product. Fixes land on
`master` and there are no backports — redeploy from the current `master` to pick up security
fixes. Notable fixes affecting already-deployed instances are called out in the
[Security section of the README](README.md#security).

## Scope

**In scope:** the ASP.NET Core application, the Terraform configuration, and `deploy.ps1` —
particularly anything affecting the authorization model, meaning who can see whose usage data
and who can administer the console.

**Out of scope:** vendored third-party libraries under `GhcpCreditVisibility/wwwroot/lib`
(report those to their upstream projects), and the configuration of your own Azure tenant.

## How this repository is reviewed

Automated security scanning with Claude Security, plus ordinary code review. The
[Security section of the README](README.md#security) records the most recent review — when it
ran, what it covered, what it deliberately did not, and what it found. That review is
point-in-time and does not replace SAST, dependency scanning, or human review.
