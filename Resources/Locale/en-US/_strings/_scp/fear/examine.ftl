examine-fear-state-anxiety = [color=lightblue]{ CAPITALIZE(gender-based-third-form) } looks anxious[/color]
examine-fear-state-fear = [color=lightblue]{ CAPITALIZE(gender-based-third-form-case) } eyes look scared[/color]
examine-fear-state-terror = [color=lightblue]{ CAPITALIZE(gender-based-third-form) } seems out of their mind![/color]
examine-fear-state-none-dead = [color=lightblue]{ CAPITALIZE(gender-based-third-form) } looks calm, as if death came unexpectedly[/color]
examine-fear-state-anxiety-dead = [color=lightblue]In { gender-based-third-form-case } dead eyes, the last frightened glance is frozen, staring into your still-living soul[/color]
examine-fear-state-fear-dead = [color=lightblue]In { gender-based-third-form-case } wide-open eyes is etched a conscious instant that became their last[/color]
examine-fear-state-terror-dead = [color=lightblue]{ CAPITALIZE(gender-based-third-form-case) } mouth is frozen in a silent scream, and the eyes look into an abyss no one should have seen[/color]
gender-based-third-form =
    { GENDER($target) ->
        [male] he
        [female] she
        [epicene] they
       *[neuter] it
    }
gender-based-third-form-case =
    { GENDER($target) ->
        [male] his
        [female] her
        [epicene] their
       *[neuter] its
    }
