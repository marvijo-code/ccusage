---
name: context7-mcp
description: Fetches current package and framework documentation through Context7. Use when implementation depends on external library APIs, configuration, examples, or version-specific behavior.
---

# Context7 MCP

Use the project-scoped Context7 server through `mcporter`:

1. Resolve the library ID:

   ```sh
   mcporter call 'context7.resolve-library-id(query: "<task>", libraryName: "<library>")'
   ```

2. Query the resolved library:

   ```sh
   mcporter call 'context7.query-docs(libraryId: "<library-id>", query: "<question>")'
   ```

Keep the query specific to the implementation question. Resolve the ID first instead of guessing it.
