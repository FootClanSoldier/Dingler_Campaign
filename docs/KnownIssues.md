# Known Issues & Limitations

This document tracks known bugs, limitations, and unresolved behavior in the HEX PvE campaign restoration project.

## Known Issues

### Mercenary Deck Not Refreshing After Save

When saving a Mercenary deck that receives a new template ID, switching back to the champion and then to the Mercenary within the same editor may display the old or empty deck.

The deck is saved correctly on the server, but the original HEX client appears to retain an outdated party-member reference.

**Workaround:** Close and reopen the deck editor to display the correct deck.

**Status:** Known client-side UI issue.

**Priority:** Low.

**Notes:** No reliable server-side fix has been found so far. A client-side patch may be required.

---
