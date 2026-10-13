extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Cards
{ 
    public static class CardSnapshotFactory 
    {
        public static CardSnapshot Create(Card card)
        {
            var hash = HashCard(card);
            return new CardSnapshot(card.m_SessionCardId, hash);
        }

        public static int HashCard(Card card)
        {
            var context = card.GetCardContext();
            var prime = 17;

            var hash = HashCode.Combine(prime, HashCardContext(context));

            foreach (var threshold in card.Thresholds)
            {
                hash = HashCode.Combine(hash, threshold.GetHashCode());
            }

            hash = HashCode.Combine(hash, card.CurrentCardState, card.CurrentAttackValue, card.CurrentHealthValue, card.EscalationCount);
            
            
            if (card.m_CurrentCardCollection == ECardCollections.Deck)
                hash = HashCode.Combine(hash, card.IsOnChain());
            
            hash = HashAllTACs(context, hash);
            var abilities = card.CurrentAbilities.ToList();
            abilities.Sort(static (a, b) => a.guid.CompareTo(b.guid));;
            foreach (var abilityId in abilities)
            {
                hash = HashCode.Combine(hash, abilityId.GetHashCode());
            }
            
            var integerVariables = new List<KeyValuePair<string, int>>(card.CardIntegerVariables);
            integerVariables.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var variable in integerVariables)
            {
                hash = HashCode.Combine(hash, variable.Key, variable.Value);
            }
            
            var counters = new List<KeyValuePair<CardCounterTemplate, int>>(card.Counters);
            counters.Sort(static (a, b) => a.Key.m_CardCounterId.guid.CompareTo(b.Key.m_CardCounterId.guid));

            foreach (var counter in counters)
            {
                var guid = counter.Key.m_CardCounterId.guid;
                var value = counter.Value;
                hash = HashCode.Combine(hash, guid, value);
            }

            hash = HashAbilityUseCounts(hash, card.m_PerGameUses);
            hash = HashAbilityUseCounts(hash, card.m_PerTurnUses);
            hash = HashAbilityUseCounts(hash, card.m_Cooldowns);
            
            return hash;
        }

        private static int HashAllTACs(CardContext context, int hash)
        {
            hash = HashIntAttrs(context, hash);
            hash = HashStringAttrs(context, hash);
            return hash;
        }

        private static int HashIntAttrs(CardContext context, int hash)
        {
            var attrs = new List<KeyValuePair<IntAttrs, int>>(context.GetCurrentIntAttrs());
            attrs.Sort(static (a, b) => string.CompareOrdinal(a.Key.name, b.Key.name));

            foreach (var attr in attrs)
            {
                var key = attr.Key;
                if (!key.NotifyClient || key == IntAttrs.TotalResources || key == IntAttrs.CurrentResources)
                    continue;

                hash = HashCode.Combine(hash, key.name, attr.Value);
            }

            return hash;
        }

        private static int HashStringAttrs(CardContext context, int hash)
        {
            var attrs = new List<KeyValuePair<StringAttrs, string>>(context.GetCurrentStringAttrs());
            attrs.Sort(static (a, b) => string.CompareOrdinal(a.Key.name, b.Key.name));

            foreach (var attr in attrs)
            {
                if (!attr.Key.NotifyClient)
                    continue;

                hash = HashCode.Combine(hash, attr.Key.name, attr.Value);
            }

            return hash;
        }

        private static int HashCardContext(CardContext context)
        {
            var hash = HashCode.Combine(context.m_CurrentType, context.m_CurrentSubtype,
                context.m_CurrentAttackValue, context.m_CurrentDefenseValue, context.m_CurrentResourceCost,
                context.m_CastingCostAdjustment, context.m_CurrentAttributeFlags);

            hash = HashCode.Combine(hash, context.m_CurrentThresholds);

            return hash;
        }

        private static int HashAbilityUseCounts(int hash, Dictionary<ResourceId, int> counts)
        {
            var list = new List<KeyValuePair<ResourceId, int>>(counts);
            list.Sort(static (a, b) => a.Key.guid.CompareTo(b.Key.guid));
            foreach (var kvp in list)
            {
                hash = HashCode.Combine(hash, kvp.Key, kvp.Value);
            }

            return hash;
        }
    }
}
