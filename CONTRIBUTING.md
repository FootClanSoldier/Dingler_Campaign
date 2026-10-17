# Contributing

- LLM usage
  - LLMs PRs are now allowed but will be given a higher level of scrutiny during review until a baseline level of competency is determined. This is nothing against the developer who made the PR, the owner just wants to make sure he understands the code base completely in order to maintain it in the future.

- Do not use Harmony Patches for anything outside of the following
  - Structural changes
     - The original engine has no way to check time remaining in a game without constant polling. Patches were used to allow it to run off of a Timer that would raise an event when someone ran out
  - Avoiding Unity Landmines
    - Some features only work under unity. For example, the card Pippit Hustler calls Unity specific code and will crash the server. We use a patch to avoid those calls.
