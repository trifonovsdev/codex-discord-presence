# Security policy

Please report vulnerabilities privately through GitHub Security Advisories instead of a public issue.

The daemon listens only on `127.0.0.1`, does not accept remote network connections, and does not use Discord credentials. Remote workspace support invokes the system OpenSSH client and expects key-based authentication.

Before sharing logs or health output, redact hostnames, usernames, project names, task IDs, and file paths if they are sensitive.
