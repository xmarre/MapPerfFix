from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding='utf-8-sig')

old = '''                        mapInteractionEntities.Add(gameEntity);
                    }
                    ____gateBannerEntitiesWithLevels = bannerEntitiesByLevel;
'''
new = '''                        // Keep banner placeholders alive. SettlementVisual.SetSettlementLevelVisibility
                        // retains and updates these exact GameEntity instances after startup.
                        // Removing them here leaves stale native pointers in
                        // _gateBannerEntitiesWithLevels and causes an AccessViolationException
                        // during RefreshPartyIcon/ValidateState on save load.
                    }
                    ____gateBannerEntitiesWithLevels = bannerEntitiesByLevel;
'''

count = source.count(old)
if count != 1:
    raise RuntimeError(f'expected one banner removal marker, found {count}')
source = source.replace(old, new, 1)
path.write_text(source, encoding='utf-8', newline='\n')
