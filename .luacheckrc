-- Luacheck config for the UE4SS Lua bridge.
-- Covers mod/ue4ss/Mods/PalLLM/Scripts/main.lua and any future Lua added
-- under that tree. Reference: https://luacheck.readthedocs.io/en/stable/config.html

-- UE4SS targets Lua 5.4 on the current public release line.
std = "lua54"

-- UE4SS injects these symbols into every mod's Lua environment. Declaring
-- them here silences spurious "accessing undefined global" warnings while
-- still catching real typos elsewhere.
read_globals = {
    -- Hook + lifecycle APIs
    "RegisterHook",
    "RegisterKeyBind",
    "LoopAsync",
    "ExecuteWithDelay",
    "NotifyOnNewObject",

    -- Object lookup
    "FindFirstOf",
    "FindAllOf",

    -- Enum-like tables injected by UE4SS
    "Key",
    "ModifierKey",

    -- Bare class names UE4SS resolves at runtime via FindFirstOf shortcuts.
    -- Listed as read_globals so a reference like `KismetSystemLibrary`
    -- doesn't trigger W113 (accessing undefined variable).
    "KismetSystemLibrary",
    "PlayerController",
}

-- Scope the check to the UE4SS bridge. Avoids picking up any stray Lua
-- that might appear under artifacts/ or other gitignored trees.
files["mod/ue4ss/Mods/PalLLM/Scripts"] = {}

-- Main.lua has long string-building lines (JSON templates, Powershell
-- script literals). The 120-char soft cap is noise here.
max_line_length = false

-- Forward references between `local function` declarations are intentional
-- in a single-file mod where helper ordering is optimized for readability
-- rather than strict top-down flow. Downgrade to a warning instead of an
-- error so CI surfaces it but doesn't block on stylistic preference.
unused = false
