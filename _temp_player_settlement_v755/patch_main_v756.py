from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")

marker = "        protected override void OnGameStart(Game game, IGameStarter starterObject)"
if marker not in source:
    raise RuntimeError("OnGameStart marker not found")

method = """        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            SaveHandler.Instance.OnApplicationTick(dt);
        }

"""

if "SaveHandler.Instance.OnApplicationTick(dt);" not in source:
    source = source.replace(marker, method + marker, 1)

path.write_text(source, encoding="utf-8")
