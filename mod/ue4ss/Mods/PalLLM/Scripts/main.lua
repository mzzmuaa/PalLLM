local mod_name = "PalLLM"
math.randomseed(os.time())

local function join_path(...)
    local parts = { ... }
    return table.concat(parts, "\\")
end

local function parent_path(path)
    local normalized = tostring(path or "")
    return normalized:match("^(.*)[/\\][^/\\]+$") or ""
end

local function current_script_path()
    local ok, info = pcall(function()
        return debug.getinfo(1, "S")
    end)

    if not ok or info == nil then
        return ""
    end

    local source = tostring(info.source or "")
    if source:sub(1, 1) == "@" then
        return source:sub(2)
    end

    return ""
end

-- Directory creations shell out to cmd.exe via os.execute, which is slow
-- (tens of ms per call on Windows). Every producer used to call ensure_dir
-- on every event write even though the Bridge paths never disappear after
-- startup. Cache the paths we've already created so the shell-out only fires
-- once per unique path for the life of the Lua module.
local ensured_dirs = {}
local function ensure_dir(path)
    if ensured_dirs[path] then
        return
    end
    os.execute(string.format('if not exist "%s" mkdir "%s"', path, path))
    ensured_dirs[path] = true
end

local function escape_json(value)
    local text = tostring(value or "")
    text = text:gsub("\\", "\\\\")
    text = text:gsub("\"", "\\\"")
    text = text:gsub("\r", "\\r")
    text = text:gsub("\n", "\\n")
    return text
end

local function quote(value)
    return "\"" .. escape_json(value) .. "\""
end

local function json_object(parts)
    return "{" .. table.concat(parts, ",") .. "}"
end

local function json_array(parts)
    return "[" .. table.concat(parts, ",") .. "]"
end

local function safe_call(fn)
    local ok, result = pcall(fn)
    if ok then
        return result
    end
    return nil
end

local function now_seconds()
    return os.time()
end

local function trim(value)
    return tostring(value or ""):gsub("^%s+", ""):gsub("%s+$", "")
end

local function unwrap_handle(value)
    if value == nil then
        return nil
    end

    return safe_call(function() return value:get() end) or value
end

local local_app_data = os.getenv("LOCALAPPDATA") or "."
local runtime_root = join_path(local_app_data, "Pal", "Saved", "PalLLM")
local runtime_config_root = join_path(runtime_root, "Config")
local bridge_root = join_path(runtime_root, "Bridge")
local bridge_inbox = join_path(bridge_root, "Inbox")
local bridge_outbox = join_path(bridge_root, "Outbox")
local bridge_archive = join_path(bridge_root, "Archive")
local bridge_screenshots = join_path(bridge_root, "Screenshots")
local bridge_diagnostics = join_path(bridge_root, "Diagnostics")

ensure_dir(bridge_root)
ensure_dir(bridge_inbox)
ensure_dir(bridge_outbox)
ensure_dir(bridge_archive)
ensure_dir(bridge_screenshots)
ensure_dir(bridge_diagnostics)

-- Screenshot cadence. 20s is a sensible default: too short and the vision model
-- gets hammered, too long and the auxiliary sensor falls behind the action. Tune
-- via the screenshot_interval_ms variable below.
local screenshot_interval_ms = 20000
local live_travel_enabled = true
local live_travel_poll_interval_ms = 6000
local live_travel_sector_size_units = 4500
local live_travel_min_distance_units = 3200
local live_travel_min_emit_interval_seconds = 18
local live_travel_anchor_stale_seconds = 45
local live_travel_combat_suppress_seconds = 15
local live_travel_state = {
    anchor = nil,
    last_emitted_at = 0,
    suppress_until = 0,
}

-- Second-stage automation gate. Runtime-side automation already requires an
-- explicit allowlist, but the game-side executor keeps its own kill switch and
-- allowlist so we never act on bridge data by accident.
local action_executor_enabled = true
local action_executor_dry_run = false
local action_executor_allowlist = {
    waypoint_suggest = true,
    recall_pals = true,
    request_craft_queue = true,
}
local action_executor_dedupe_ttl_seconds = 600
local action_executor_dedupe_max_entries = 256
local executed_action_keys = {}
local delivery_channel_gap_ms = 650
local delivery_dedupe_ttl_seconds = 30
local delivery_dedupe_max_entries = 128
local delivery_queue_until_ms = 0
local delivery_render_keys = {}
local delivery_queue_compact_threshold_ms = 8000
local delivery_queue_drop_threshold_ms = 16000
local delivery_queue_collapse_threshold_ms = 28000
local delivery_queue_generation = 0
local ui_probe_enabled = true
local ui_probe_auto_emit_enabled = true
local ui_probe_auto_emit_min_widgets = 4
local ui_probe_emit_cooldown_seconds = 45
local ui_probe_max_widgets = 24
local ui_probe_dump_widget_limit = 12
local ui_probe_recent_event_limit = 32
-- Phase-4 in-game delivery seam. The runtime emits full presentation plans; the
-- game-side needs to consume them through a real HUD surface. Until a specific
-- Palworld UserWidget is confirmed by the operator via /api/bridge/ui-probe, we
-- keep the richer bind OFF by default and fall through to ClientMessage.
--
-- Operators enable by listing confirmed widget FullNames here and flipping
-- native_hud_render_enabled to true. Targets are matched in order; the first
-- widget that accepts a SetText call on any reachable TextBlock child wins.
-- Every step is pcall-guarded so a wrong guess degrades cleanly instead of
-- throwing from inside the outbox poll loop.
-- Operators can also place a Lua override file at config\native-hud.lua
-- beside the installed mod (preferred) or under the runtime root Config
-- directory. The override file should return a table with
-- native_hud_render_enabled and native_hud_widget_targets. It may also set
-- native_audio_mixer_enabled and native_audio_mixer_callback_name once a real
-- Palworld mixer callback has been installed.
-- The startup bridge_boot heartbeat reports this list verbatim so the sidecar
-- can confirm whether the live bridge config matches the current ui_probe
-- recommendation.
local native_hud_render_enabled = false
local native_hud_widget_targets = {
    -- Example entries — populate from the dashboard's ranked ui_probe list:
    -- "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
    -- "/Game/UI/WBP_PlayerMessage.WBP_PlayerMessage_C",
}
-- Native audio remains default-off until a real Palworld mixer callback is
-- proven. When enabled, the bridge looks for a global Lua function with this
-- name. The callback receives a table with the local path plus content-free
-- format metadata and must return true, or { started = true }, only after the
-- engine-side mixer has actually accepted the raw PCM buffer.
local native_audio_mixer_enabled = false
local native_audio_mixer_callback_name = "PalLLM_NativeAudioMixer_PlayRawPcm"
local native_hud_config_source = "inline_defaults"
local native_hud_config_path = ""

local function normalize_hud_target_list(targets)
    if type(targets) ~= "table" then
        return {}
    end

    local normalized = {}
    local seen = {}
    for _, value in ipairs(targets) do
        local text = trim(value)
        if text ~= "" and not seen[text] then
            normalized[#normalized + 1] = text
            seen[text] = true
        end
    end

    return normalized
end

local function build_native_hud_config_candidates()
    local candidates = {}
    local script_path = current_script_path()
    if script_path ~= "" then
        local script_dir = parent_path(script_path)
        local mod_root = parent_path(script_dir)
        if mod_root ~= "" then
            candidates[#candidates + 1] = {
                source = "mod_override_file",
                path = join_path(mod_root, "config", "native-hud.lua"),
            }
        end
    end

    candidates[#candidates + 1] = {
        source = "runtime_override_file",
        path = join_path(runtime_config_root, "native-hud.lua"),
    }

    return candidates
end

local function load_native_hud_override()
    local candidates = build_native_hud_config_candidates()
    if #candidates > 0 then
        native_hud_config_path = candidates[1].path
    end

    for _, candidate in ipairs(candidates) do
        local probe = io.open(candidate.path, "rb")
        if probe then
            local body = probe:read("*all") or ""
            probe:close()
            native_hud_config_path = candidate.path

            if load == nil then
                native_hud_config_source = "override_error"
                print("[PalLLM][HudConfig] load is unavailable; using inline defaults.")
                return
            end

            if body:sub(1, 3) == "\239\187\191" then
                body = body:sub(4)
            end

            local chunk, load_error = load(body, "@" .. candidate.path, "t")
            if not chunk then
                native_hud_config_source = "override_error"
                print("[PalLLM][HudConfig] failed to parse override file: " .. tostring(load_error or "unknown error"))
                return
            end

            local ok, result = pcall(chunk)
            if not ok or type(result) ~= "table" then
                native_hud_config_source = "override_error"
                print("[PalLLM][HudConfig] override file must return a table; using inline defaults.")
                return
            end

            if type(result.native_hud_render_enabled) == "boolean" then
                native_hud_render_enabled = result.native_hud_render_enabled
            end

            if type(result.native_hud_widget_targets) == "table" then
                native_hud_widget_targets = normalize_hud_target_list(result.native_hud_widget_targets)
            end

            if type(result.native_audio_mixer_enabled) == "boolean" then
                native_audio_mixer_enabled = result.native_audio_mixer_enabled
            end

            if type(result.native_audio_mixer_callback_name) == "string" then
                local callback_name = trim(result.native_audio_mixer_callback_name)
                if callback_name ~= "" then
                    native_audio_mixer_callback_name = callback_name
                end
            end

            native_hud_config_source = candidate.source
            print("[PalLLM][HudConfig] loaded override from " .. candidate.path)
            return
        end
    end

    if native_hud_config_path ~= "" then
        print("[PalLLM][HudConfig] using inline defaults; override path " .. native_hud_config_path)
    end
end

load_native_hud_override()

-- Travel actions can also try a native map marker path before they fall back to
-- bridge feedback. Keep this enabled by default, but the runtime still reports
-- whether PalMapManager compatibility was actually observed during boot.
local waypoint_native_marker_enabled = true

-- The runtime understands `production` events, but Palworld's crafting loop
-- does not expose a clean single-shot broadcast the way chat/raid/combat do.
-- This sampler polls BaseCampManager on a long interval and only emits when
-- the item+status tuple for a base has actually changed. Defaults to off until
-- operators confirm hook names against their build; when on, the poll cadence
-- is intentionally large and bounded so this cannot create a hot loop.
local production_sampler_enabled = false
local ui_probe_state = {
    widgets = {},
    order = {},
    recent_events = {},
    hooks_installed = false,
    keybind_registered = false,
    last_emit_at = 0,
}

local function write_event(event_type, payload_json)
    ensure_dir(bridge_inbox)
    local stamp = os.date("!%Y%m%dT%H%M%SZ")
    local nonce = math.random(100000, 999999)
    local file_name = join_path(bridge_inbox, string.format("%s-%s-%s-%d.json", mod_name, event_type, stamp, nonce))
    local file = io.open(file_name, "w")
    if not file then
        print("[PalLLM] failed to write bridge event: " .. file_name)
        return
    end

    local envelope = json_object({
        "\"EventType\":" .. quote(event_type),
        "\"Source\":" .. quote("ue4ss"),
        "\"TimestampUtc\":" .. quote(os.date("!%Y-%m-%dT%H:%M:%SZ")),
        "\"Payload\":" .. payload_json
    })

    file:write(envelope)
    file:close()
end

local function parse_vector_text(text)
    local x, y, z = text:match("X%s*=%s*([%-%.%d]+)[,%s]+Y%s*=%s*([%-%.%d]+)[,%s]+Z%s*=%s*([%-%.%d]+)")
    if not x then
        x, y, z = text:match("%(?%s*([%-%.%d]+)%s*,%s*([%-%.%d]+)%s*,%s*([%-%.%d]+)%s*%)?")
    end

    x = tonumber(x)
    y = tonumber(y)
    z = tonumber(z)
    if x == nil or y == nil or z == nil then
        return nil
    end

    return x, y, z
end

local function read_vector_components(value)
    if value == nil then
        return nil
    end

    local x = safe_call(function() return value.X end) or safe_call(function() return value.x end)
    local y = safe_call(function() return value.Y end) or safe_call(function() return value.y end)
    local z = safe_call(function() return value.Z end) or safe_call(function() return value.z end)
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then
        return x, y, z
    end

    return parse_vector_text(tostring(value))
end

local function classify_vertical_band(z)
    if z >= 2400 then
        return "ridge"
    end
    if z <= -600 then
        return "lowland"
    end

    return "ground"
end

local function build_sector_label(sample)
    return string.format(
        "sector %d,%d %s",
        sample.sector_x,
        sample.sector_y,
        sample.band)
end

local function take_first_nonempty(...)
    for index = 1, select("#", ...) do
        local candidate = trim(select(index, ...))
        if candidate ~= "" then
            return candidate
        end
    end

    return ""
end

local function build_ui_probe_display_name(full_name, fallback_name, class_name)
    local candidate = take_first_nonempty(fallback_name)
    if candidate ~= "" and candidate ~= "nil" then
        return candidate
    end

    local normalized_full_name = trim(full_name)
    if normalized_full_name ~= "" then
        local object_name = normalized_full_name:match("([%w_]+)%s*$")
        if trim(object_name) ~= "" then
            return object_name
        end

        return normalized_full_name
    end

    return take_first_nonempty(class_name, "UnknownWidget")
end

local function describe_ui_probe_widget(widget)
    local object = unwrap_handle(widget)
    if object == nil then
        return nil
    end

    local is_valid = safe_call(function() return object:IsValid() end)
    if is_valid == false then
        return nil
    end

    local full_name = safe_call(function() return object:GetFullName() end) or ""
    local fallback_name = safe_call(function() return tostring(object) end) or ""
    local class_name = safe_call(function()
        local class_object = object:GetClass()
        if class_object and class_object.GetFullName then
            return class_object:GetFullName()
        end
        return tostring(class_object)
    end) or ""

    local display_name = build_ui_probe_display_name(full_name, fallback_name, class_name)
    local key = take_first_nonempty(full_name, class_name, display_name)
    if key == "" then
        return nil
    end

    return {
        key = key,
        display_name = display_name,
        full_name = trim(full_name),
        class_name = trim(class_name),
    }
end

local function count_active_ui_probe_widgets()
    local count = 0
    for _, key in ipairs(ui_probe_state.order) do
        local entry = ui_probe_state.widgets[key]
        if entry and entry.is_active then
            count = count + 1
        end
    end

    return count
end

local function prune_ui_probe_widgets()
    while #ui_probe_state.order > ui_probe_max_widgets do
        local removal_index = nil
        for index, key in ipairs(ui_probe_state.order) do
            local entry = ui_probe_state.widgets[key]
            if entry == nil or not entry.is_active then
                removal_index = index
                break
            end
        end

        if removal_index == nil then
            removal_index = 1
        end

        local removed_key = table.remove(ui_probe_state.order, removal_index)
        if removed_key then
            ui_probe_state.widgets[removed_key] = nil
        end
    end
end

local function record_ui_probe_recent_event(entry, lifecycle)
    local label = take_first_nonempty(entry.display_name, entry.class_name, entry.full_name, "UnknownWidget")
    ui_probe_state.recent_events[#ui_probe_state.recent_events + 1] = string.format(
        "%s | %s | %s",
        os.date("!%Y-%m-%dT%H:%M:%SZ"),
        lifecycle,
        label)

    while #ui_probe_state.recent_events > ui_probe_recent_event_limit do
        table.remove(ui_probe_state.recent_events, 1)
    end
end

local function collect_ui_probe_entries(limit)
    local entries = {}
    for _, key in ipairs(ui_probe_state.order) do
        local entry = ui_probe_state.widgets[key]
        if entry then
            entries[#entries + 1] = entry
        end
    end

    table.sort(entries, function(left, right)
        if (left.is_active and 1 or 0) ~= (right.is_active and 1 or 0) then
            return left.is_active
        end
        if (left.seen_count or 0) ~= (right.seen_count or 0) then
            return (left.seen_count or 0) > (right.seen_count or 0)
        end
        local left_label = take_first_nonempty(left.display_name, left.class_name, left.full_name)
        local right_label = take_first_nonempty(right.display_name, right.class_name, right.full_name)
        return left_label < right_label
    end)

    if limit and #entries > limit then
        local trimmed = {}
        for index = 1, limit do
            trimmed[#trimmed + 1] = entries[index]
        end
        return trimmed
    end

    return entries
end

local function build_ui_probe_summary(entries)
    local observed_count = #ui_probe_state.order
    local active_count = count_active_ui_probe_widgets()
    if observed_count == 0 then
        return "No widgets observed yet."
    end

    local labels = {}
    local limit = math.min(4, #entries)
    for index = 1, limit do
        local entry = entries[index]
        local label = take_first_nonempty(entry.display_name, entry.class_name, entry.full_name)
        if entry.seen_count and entry.seen_count > 1 then
            label = label .. " x" .. tostring(entry.seen_count)
        end
        if entry.is_active then
            label = label .. " active"
        end
        labels[#labels + 1] = label
    end

    return string.format(
        "%d observed, %d active | %s",
        observed_count,
        active_count,
        table.concat(labels, " | "))
end

local function serialize_ui_probe_widget_entry(entry)
    return json_object({
        "\"DisplayName\":" .. quote(entry.display_name or ""),
        "\"FullName\":" .. quote(entry.full_name or ""),
        "\"ClassName\":" .. quote(entry.class_name or ""),
        "\"SeenCount\":" .. tostring(math.max(0, entry.seen_count or 0)),
        "\"IsActive\":" .. ((entry.is_active and "true") or "false"),
        "\"LastLifecycle\":" .. quote(entry.last_lifecycle or "")
    })
end

local function write_ui_probe_dump(reason, summary, entries)
    ensure_dir(bridge_diagnostics)
    local stamp = os.date("!%Y%m%dT%H%M%SZ")
    local nonce = math.random(100000, 999999)
    local path = join_path(bridge_diagnostics, string.format("palllm-ui-probe-%s-%d.json", stamp, nonce))
    local file = io.open(path, "w")
    if not file then
        print("[PalLLM][UIProbe] failed to write diagnostic dump: " .. path)
        return ""
    end

    local widget_parts = {}
    for _, entry in ipairs(entries) do
        widget_parts[#widget_parts + 1] = serialize_ui_probe_widget_entry(entry)
    end

    local recent_parts = {}
    for _, event_text in ipairs(ui_probe_state.recent_events) do
        recent_parts[#recent_parts + 1] = quote(event_text)
    end

    local body = json_object({
        "\"GeneratedAtUtc\":" .. quote(os.date("!%Y-%m-%dT%H:%M:%SZ")),
        "\"Reason\":" .. quote(reason or ""),
        "\"Summary\":" .. quote(summary or ""),
        "\"ObservedWidgetCount\":" .. tostring(#ui_probe_state.order),
        "\"ActiveWidgetCount\":" .. tostring(count_active_ui_probe_widgets()),
        "\"Widgets\":" .. json_array(widget_parts),
        "\"RecentEvents\":" .. json_array(recent_parts)
    })

    file:write(body)
    file:close()
    return path
end

local function emit_ui_probe(reason, force)
    if not ui_probe_enabled then
        return false
    end

    local now = now_seconds()
    if not force and (now - (ui_probe_state.last_emit_at or 0)) < ui_probe_emit_cooldown_seconds then
        return false
    end

    local entries = collect_ui_probe_entries(ui_probe_dump_widget_limit)
    local summary = build_ui_probe_summary(entries)
    local dump_path = write_ui_probe_dump(reason, summary, entries)
    local widget_parts = {}
    for _, entry in ipairs(entries) do
        widget_parts[#widget_parts + 1] = serialize_ui_probe_widget_entry(entry)
    end

    write_event("ui_probe", json_object({
        "\"Reason\":" .. quote(reason or ""),
        "\"Summary\":" .. quote(summary),
        "\"DumpPath\":" .. quote(dump_path),
        "\"ObservedWidgetCount\":" .. tostring(#ui_probe_state.order),
        "\"ActiveWidgetCount\":" .. tostring(count_active_ui_probe_widgets()),
        "\"Widgets\":" .. json_array(widget_parts)
    }))

    ui_probe_state.last_emit_at = now
    print("[PalLLM][UIProbe] " .. summary)
    if dump_path ~= "" then
        print("[PalLLM][UIProbe] dump: " .. dump_path)
    end
    return true
end

local function record_ui_probe_widget_lifecycle(widget, lifecycle)
    if not ui_probe_enabled then
        return
    end

    local descriptor = describe_ui_probe_widget(widget)
    if descriptor == nil then
        return
    end

    local entry = ui_probe_state.widgets[descriptor.key]
    local is_new = false
    if not entry then
        entry = {
            display_name = descriptor.display_name,
            full_name = descriptor.full_name,
            class_name = descriptor.class_name,
            seen_count = 0,
            is_active = false,
            last_lifecycle = "",
        }
        ui_probe_state.widgets[descriptor.key] = entry
        ui_probe_state.order[#ui_probe_state.order + 1] = descriptor.key
        is_new = true
    end

    entry.display_name = take_first_nonempty(descriptor.display_name, entry.display_name)
    entry.full_name = take_first_nonempty(descriptor.full_name, entry.full_name)
    entry.class_name = take_first_nonempty(descriptor.class_name, entry.class_name)
    entry.seen_count = math.max(0, entry.seen_count or 0) + 1
    entry.is_active = lifecycle ~= "destruct"
    entry.last_lifecycle = lifecycle

    prune_ui_probe_widgets()
    record_ui_probe_recent_event(entry, lifecycle)

    if ui_probe_auto_emit_enabled
        and is_new
        and #ui_probe_state.order >= ui_probe_auto_emit_min_widgets then
        emit_ui_probe("auto_widget_sample", false)
    end
end

local function install_ui_probe_hooks()
    if not ui_probe_enabled or ui_probe_state.hooks_installed then
        return
    end

    local installed = safe_call(function()
        RegisterHook("/Script/UMG.UserWidget:Construct", function(widget)
            record_ui_probe_widget_lifecycle(widget, "construct")
        end)
        RegisterHook("/Script/UMG.UserWidget:Destruct", function(widget)
            record_ui_probe_widget_lifecycle(widget, "destruct")
        end)
        return true
    end)

    if installed then
        ui_probe_state.hooks_installed = true
        print("[PalLLM][UIProbe] watching /Script/UMG.UserWidget:Construct and :Destruct")
    else
        print("[PalLLM][UIProbe] failed to install widget lifecycle hooks")
    end
end

local function register_ui_probe_keybind()
    if not ui_probe_enabled or ui_probe_state.keybind_registered then
        return
    end

    local registered = safe_call(function()
        RegisterKeyBind(Key.U, { ModifierKey.CONTROL, ModifierKey.SHIFT }, function()
            emit_ui_probe("manual_keybind", true)
        end)
        return true
    end)

    if registered then
        ui_probe_state.keybind_registered = true
        print("[PalLLM][UIProbe] press CTRL+SHIFT+U to dump widget candidates")
    else
        print("[PalLLM][UIProbe] failed to register CTRL+SHIFT+U keybind")
    end
end

-- Resilient hook registration. Wraps RegisterHook in pcall and records the
-- result so a renamed Palworld class (different game patch) degrades to a
-- no-op instead of crashing the mod or silently losing events. The bridge_boot
-- event reports which hooks installed so the sidecar dashboard and
-- `/api/health` can surface compat status at a glance.
local registered_hooks = {}
local function register_hook_safely(hook_path, handler)
    local ok, err = pcall(function()
        RegisterHook(hook_path, handler)
    end)
    registered_hooks[hook_path] = ok
    if not ok then
        print(string.format("[PalLLM][Compat] hook registration failed: %s (%s)", hook_path, tostring(err)))
    end
    return ok
end

local function run_compat_probe()
    -- Each entry is { target-key, UE-path-or-class-name }. "class" entries use
    -- FindFirstOf / FindAllOf so the probe runs even before any instance is
    -- spawned; "hook" entries are reported after register_hook_safely has been
    -- called and recorded its outcome. Meant to give operators a quick read of
    -- what survived the current Palworld patch.
    local probes = {
        { kind = "class", key = "PalGameStateInGame", lookup = "PalGameStateInGame" },
        { kind = "class", key = "PalCharacter", lookup = "PalCharacter" },
        { kind = "class", key = "PalWeatherManager", lookup = "PalWeatherManager" },
        { kind = "class", key = "PalBaseCampManager", lookup = "PalBaseCampManager" },
        { kind = "class", key = "PalMapManager", lookup = "PalMapManager" },
        { kind = "class", key = "UserWidget", lookup = "UserWidget" },
    }

    local lines = {}
    local signal_parts = {}
    for _, probe in ipairs(probes) do
        local resolved
        if probe.kind == "class" then
            resolved = safe_call(function() return FindFirstOf(probe.lookup) end)
        end
        local present = resolved and true or false
        lines[#lines + 1] = string.format("%s=%s", probe.key, present and "present" or "missing")
        signal_parts[#signal_parts + 1] = json_object({
            "\"Key\":" .. quote(probe.key),
            "\"Present\":" .. (present and "true" or "false")
        })
    end
    return table.concat(lines, " | "), json_array(signal_parts)
end

print("[PalLLM] UE4SS bridge booting")
local compat_summary, compat_signals = run_compat_probe()
local native_hud_target_parts = {}
for _, target_name in ipairs(native_hud_widget_targets) do
    local normalized_target_name = trim(target_name)
    if normalized_target_name ~= "" then
        native_hud_target_parts[#native_hud_target_parts + 1] = quote(normalized_target_name)
    end
end
print("[PalLLM][Compat] " .. compat_summary)
write_event("bridge_boot", json_object({
    "\"Version\":" .. quote("0.3.0"),
    "\"Status\":" .. quote("booted"),
    "\"Compat\":" .. quote(compat_summary),
    "\"CompatSignals\":" .. compat_signals,
    "\"UiProbeEnabled\":" .. ((ui_probe_enabled and "true") or "false"),
    "\"ActionExecutorEnabled\":" .. ((action_executor_enabled and "true") or "false"),
    "\"NativeHudRenderEnabled\":" .. ((native_hud_render_enabled and "true") or "false"),
    "\"NativeHudWidgetTargetCount\":" .. tostring(#native_hud_widget_targets),
    "\"NativeHudWidgetTargets\":" .. json_array(native_hud_target_parts),
    "\"NativeHudConfigSource\":" .. quote(native_hud_config_source),
    "\"NativeHudConfigPath\":" .. quote(native_hud_config_path),
    "\"ProductionSamplerEnabled\":" .. ((production_sampler_enabled and "true") or "false"),
    "\"WaypointNativeMarkerEnabled\":" .. ((waypoint_native_marker_enabled and "true") or "false")
}))
install_ui_probe_hooks()
register_ui_probe_keybind()

-- =====================================================================
-- Event producers
-- =====================================================================

-- Chat capture via Palworld's chat broadcast hook.
register_hook_safely("/Script/Pal.PalGameStateInGame:BroadcastChatMessage", function(self, ChatMessage)
    local chat_message = safe_call(function() return ChatMessage:get() end)
    if chat_message == nil then return end

    local sender = safe_call(function() return chat_message.Sender:ToString() end) or ""
    local message = safe_call(function() return chat_message.Message:ToString() end) or ""
    if message == "" then return end

    write_event("chat_message", json_object({
        "\"Sender\":" .. quote(sender),
        "\"Message\":" .. quote(message),
        "\"Category\":" .. quote("chat")
    }))

    print("[PalLLM] captured chat: " .. message)
end)

-- Base discovery — installed once after the first possession acknowledgement.
local base_hook_installed = false
register_hook_safely("/Script/Engine.PlayerController:ServerAcknowledgePossession", function()
    install_ui_probe_hooks()
    register_ui_probe_keybind()

    if base_hook_installed then return end
    base_hook_installed = true

    ExecuteWithDelay(5000, function()
        NotifyOnNewObject("/Script/Pal.PalBaseCampModel", function(base_model)
            local area_range = "null"
            pcall(function() area_range = tostring(base_model.AreaRange) end)

            write_event("base_discovered", json_object({
                "\"BaseId\":" .. quote(tostring(base_model)),
                "\"AreaRange\":" .. area_range
            }))

            print("[PalLLM] discovered base model")
        end)
    end)
end)

-- Combat start/end — lightweight producers hooked on damage / death events.
-- The PalLLM runtime only needs a phase and an opponent name to update world state.
local function emit_combat_event(phase, opponent, location)
    if phase ~= "end" then
        live_travel_state.suppress_until = math.max(
            live_travel_state.suppress_until or 0,
            now_seconds() + live_travel_combat_suppress_seconds)
    end

    write_event(phase == "end" and "combat_end" or "combat_start", json_object({
        "\"Phase\":" .. quote(phase or "start"),
        "\"Opponent\":" .. quote(opponent or "unknown"),
        "\"Location\":" .. quote(location or "")
    }))
end

register_hook_safely("/Script/Pal.PalCharacter:ReceiveAnyDamage", function(self, damage, damage_type, instigator)
    local opponent = safe_call(function() return instigator and tostring(instigator:get()) end) or "unknown"
    local location = safe_call(function() return tostring(self:get():GetLocation()) end) or ""
    emit_combat_event("start", opponent, location)
end)

register_hook_safely("/Script/Pal.PalCharacter:OnDead_ToBP", function(self)
    local pal_name = safe_call(function() return tostring(self:get()) end) or "unknown"
    write_event("pal_status", json_object({
        "\"PalName\":" .. quote(pal_name),
        "\"Species\":" .. quote(""),
        "\"Change\":" .. quote("downed"),
        "\"Note\":" .. quote("")
    }))
    emit_combat_event("end", pal_name, "")
end)

-- Weather changes — best-effort producer that fires when the weather manager ticks.
register_hook_safely("/Script/Pal.PalWeatherManager:Notify_OnWeatherChanged", function(self, new_weather, biome, severity)
    local weather_name = safe_call(function() return tostring(new_weather:get()) end) or "unknown"
    local biome_name = safe_call(function() return tostring(biome and biome:get()) end) or ""
    local severity_name = safe_call(function() return tostring(severity and severity:get()) end) or "mild"

    write_event("weather_change", json_object({
        "\"Weather\":" .. quote(weather_name),
        "\"Biome\":" .. quote(biome_name),
        "\"Severity\":" .. quote(severity_name)
    }))
end)

-- Raid start — fired by the AI manager when a raid spawns against a known base.
register_hook_safely("/Script/Pal.PalGameStateInGame:Notify_OnInvaderSpawned", function(self, invader, base_id)
    local faction = safe_call(function() return tostring(invader and invader:get()) end) or "unknown"
    local base = safe_call(function() return tostring(base_id and base_id:get()) end) or "unknown"

    write_event("raid", json_object({
        "\"BaseId\":" .. quote(base),
        "\"Faction\":" .. quote(faction),
        "\"AttackerCount\":null",
        "\"Phase\":" .. quote("incoming"),
        "\"Note\":" .. quote("")
    }))
end)

-- Production sampler is installed later in the file, after trim and
-- to_positive_int helpers are defined. Placing the LoopAsync bind up here would
-- reference forward-declared functions and silently no-op. See the block near
-- the end of this file.

-- =====================================================================
-- Outbox consumer
-- =====================================================================
-- PalLLM writes chat_reply envelopes into Bridge/Outbox. The Lua side polls
-- on a short interval, renders the assistant message plus cue metadata,
-- optionally attempts speech playback for linked TTS artifacts, and archives
-- the processed file so the sidecar can see it was consumed.

local function list_outbox_files()
    local pipe = io.popen(string.format('dir /B "%s\\*.json" 2>nul', bridge_outbox))
    if not pipe then return {} end
    local files = {}
    for line in pipe:lines() do
        if line and line ~= "" then
            files[#files + 1] = join_path(bridge_outbox, line)
        end
    end
    pipe:close()
    return files
end

local function read_all(path)
    local file = io.open(path, "r")
    if not file then return nil end
    local body = file:read("*all")
    file:close()
    return body
end

-- Tiny extraction — we don't embed a JSON parser but the envelope shape is
-- known so a couple of simple string searches are enough to pull the two
-- fields the renderer needs.
local function decode_escaped_text(value)
    local text = tostring(value or "")
    text = text:gsub("\\r", "\r")
    text = text:gsub("\\n", "\n")
    text = text:gsub("\\\"", "\"")
    text = text:gsub("\\\\", "\\")
    return text
end

local function humanize_identifier(value)
    local text = trim(decode_escaped_text(value))
    if text == "" then
        return ""
    end

    text = text:gsub("[%+_%-]", " ")
    text = text:gsub("%s+", " ")
    return text:gsub("(%a)([%w']*)", function(first, rest)
        return string.upper(first) .. string.lower(rest)
    end)
end

local function join_nonempty(values, separator)
    local parts = {}
    for _, value in ipairs(values) do
        local text = trim(value)
        if text ~= "" then
            parts[#parts + 1] = text
        end
    end

    return table.concat(parts, separator or " | ")
end

local function extract_field(body, name)
    local pattern = string.format('"%s"%%s*:%%s*"(.-)"', name)
    return body:match(pattern)
end

local function extract_boolean(body, name)
    local pattern = string.format('"%s"%%s*:%%s*(true|false)', name)
    local value = body:match(pattern)
    if value == "true" then
        return true
    end
    if value == "false" then
        return false
    end
    return nil
end

local function extract_integer(body, name)
    local pattern = string.format('"%s"%%s*:%%s*(-?%%d+)', name)
    local value = body:match(pattern)
    if not value then
        return nil
    end

    return tonumber(value)
end

local function extract_object_body(body, name)
    local _, key_end = body:find('"' .. name .. '"%s*:%s*{')
    if not key_end then
        return nil
    end

    local brace_start = body:find("{", key_end, true)
    if not brace_start then
        return nil
    end

    local depth = 0
    local in_string = false
    local escaped = false
    for i = brace_start, #body do
        local ch = body:sub(i, i)
        if in_string then
            if escaped then
                escaped = false
            elseif ch == "\\" then
                escaped = true
            elseif ch == "\"" then
                in_string = false
            end
        else
            if ch == "\"" then
                in_string = true
            elseif ch == "{" then
                depth = depth + 1
            elseif ch == "}" then
                depth = depth - 1
                if depth == 0 then
                    return body:sub(brace_start, i)
                end
            end
        end
    end

    return nil
end


local function extract_string_array(body, name)
    local values = {}
    local pattern = string.format('"%s"%%s*:%%s*%[(.-)%]', name)
    local segment = body:match(pattern)
    if not segment then
        return values
    end

    for value in segment:gmatch('"(.-)"') do
        values[#values + 1] = trim(decode_escaped_text(value))
    end

    return values
end

local function collect_display_tokens(tokens, skip_tokens, max_tokens)
    local parts = {}
    if type(tokens) ~= "table" then
        return parts
    end

    local skipped = 0
    local skip_limit = math.max(0, tonumber(skip_tokens) or 0)
    local limit = max_tokens or 999
    for _, value in ipairs(tokens) do
        local text = trim(decode_escaped_text(value))
        if text ~= "" then
            if skipped < skip_limit then
                skipped = skipped + 1
            else
                parts[#parts + 1] = text
                if #parts >= limit then
                    break
                end
            end
        end
    end

    return parts
end

local function count_display_tokens(tokens)
    local count = 0
    if type(tokens) ~= "table" then
        return 0
    end

    for _, value in ipairs(tokens) do
        if trim(decode_escaped_text(value)) ~= "" then
            count = count + 1
        end
    end

    return count
end

local function join_display_tokens(tokens, separator, max_tokens, skip_tokens)
    local parts = collect_display_tokens(tokens, skip_tokens, max_tokens)
    if #parts == 0 then
        return ""
    end

    return join_nonempty(parts, separator or " | ")
end

local function truncate_with_ellipsis(text, width)
    local value = trim(tostring(text or ""))
    if width == nil or width <= 0 then
        return value
    end

    if #value <= width then
        return value
    end

    if width <= 3 then
        return value:sub(1, width)
    end

    return trim(value:sub(1, width - 3)) .. "..."
end

local function wrap_text(text, width, max_lines)
    local limit = max_lines or 999
    local lines = {}
    local paragraphs = {}
    local normalized = tostring(text or ""):gsub("\r\n", "\n")
    for paragraph in normalized:gmatch("[^\n]+") do
        paragraphs[#paragraphs + 1] = paragraph
    end

    if #paragraphs == 0 and trim(normalized) ~= "" then
        paragraphs[1] = normalized
    end

    local reached_limit = false
    for _, paragraph in ipairs(paragraphs) do
        local current = ""
        for word in paragraph:gmatch("%S+") do
            if #word > width then
                word = truncate_with_ellipsis(word, width)
            end

            if current == "" then
                current = word
            elseif (#current + 1 + #word) <= width then
                current = current .. " " .. word
            else
                lines[#lines + 1] = current
                if #lines >= limit then
                    reached_limit = true
                    break
                end

                current = word
            end
        end

        if reached_limit then
            break
        end

        if current ~= "" then
            lines[#lines + 1] = current
            if #lines >= limit then
                reached_limit = true
                break
            end
        end
    end

    if reached_limit and #lines > 0 then
        lines[#lines] = truncate_with_ellipsis(lines[#lines], width)
    end

    return lines
end

local function line_width_from_prefix(width, prefix)
    local content_width = width or 54
    local lead = trim(prefix or "") ~= "" and tostring(prefix or "") or ""
    if lead ~= "" then
        content_width = math.max(12, content_width - #lead)
    end

    return content_width
end

local function append_prefixed_line(target, prefix, text, width, max_total_lines)
    if trim(text) == "" or #target >= (max_total_lines or 999) then
        return
    end

    local lead = prefix or ""
    local content_width = line_width_from_prefix(width, lead)
    target[#target + 1] = lead .. truncate_with_ellipsis(text, content_width)
end

local function append_wrapped_prefixed_lines(target, text, prefix, width, max_total_lines)
    if trim(text) == "" then
        return
    end

    local limit = max_total_lines or 999
    local remaining = math.max(0, limit - #target)
    if remaining == 0 then
        return
    end

    local lead = prefix or ""
    local content_width = line_width_from_prefix(width, lead)
    local wrapped = wrap_text(text, content_width, remaining)
    for _, line in ipairs(wrapped) do
        target[#target + 1] = lead .. line
    end
end

local function file_exists(path)
    local file = io.open(path, "rb")
    if not file then
        return false
    end

    file:close()
    return true
end

local function file_size_bytes(path)
    local file = io.open(path, "rb")
    if not file then
        return nil
    end

    local size = file:seek("end")
    file:close()
    return size
end

local function file_extension(path)
    local normalized = trim(path):lower()
    return normalized:match("(%.[^%.\\/]+)$") or ""
end

local function clamp_audio_receipt_number(value, max_value)
    local number = tonumber(value or 0) or 0
    if number < 0 then
        return 0
    end
    if number > max_value then
        return max_value
    end

    return math.floor(number + 0.5)
end

local function mime_type_base(mime_type)
    local normalized = trim(mime_type):lower()
    local separator = normalized:find(";", 1, true)
    if separator then
        return trim(normalized:sub(1, separator - 1))
    end

    return normalized
end

local function unquote_mime_parameter(value)
    local normalized = trim(value)
    if #normalized >= 2 then
        local first = normalized:sub(1, 1)
        local last = normalized:sub(-1)
        if (first == "\"" and last == "\"") or (first == "'" and last == "'") then
            return normalized:sub(2, -2)
        end
    end

    return normalized
end

local function mime_parameter_number(mime_type, names, max_value)
    local normalized = trim(mime_type)
    for parameter in normalized:gmatch("[^;]+") do
        local equals = parameter:find("=", 1, true)
        if equals then
            local key = trim(parameter:sub(1, equals - 1)):lower()
            for _, name in ipairs(names) do
                if key == name then
                    return clamp_audio_receipt_number(unquote_mime_parameter(parameter:sub(equals + 1)), max_value)
                end
            end
        end
    end

    return 0
end

local function mime_parameter_text(mime_type, names)
    local normalized = trim(mime_type)
    for parameter in normalized:gmatch("[^;]+") do
        local equals = parameter:find("=", 1, true)
        if equals then
            local key = trim(parameter:sub(1, equals - 1)):lower()
            for _, name in ipairs(names) do
                if key == name then
                    return unquote_mime_parameter(parameter:sub(equals + 1))
                end
            end
        end
    end

    return ""
end

local function normalize_audio_byte_order(value)
    local normalized = trim(value):lower():gsub("[_%-%s]", "")
    if normalized == "be" or normalized == "bigendian" or normalized == "network" or normalized:match("be$") then
        return "big_endian"
    end

    if normalized == "le" or normalized == "littleendian" or normalized:match("le$") then
        return "little_endian"
    end

    return ""
end

local function normalize_audio_sample_format(value)
    local normalized = trim(value):lower():gsub("[_%-%s]", "")
    if normalized == "" then
        return ""
    end

    if normalized == "float" or normalized == "floatingpoint" or normalized:match("^f%d+") then
        return "float"
    end

    if normalized == "unsigned" or normalized == "unsignedinteger" or normalized:match("^u%d+") then
        return "unsigned_integer"
    end

    if normalized == "signed" or normalized == "signedinteger" or normalized == "integer" or normalized == "pcm" or normalized:match("^s%d+") then
        return "signed_integer"
    end

    if normalized == "alaw" or normalized == "mulaw" then
        return "companded"
    end

    return ""
end

local function describe_native_mixer_conversion_hint(sample_format, byte_order, bits_per_sample, channel_count)
    local normalized_format = trim(sample_format):lower()
    local normalized_order = trim(byte_order):lower()
    local bits = tonumber(bits_per_sample or 0) or 0
    local channels = tonumber(channel_count or 0) or 0
    local steps = {}

    if normalized_order == "big_endian" and bits > 8 then
        table.insert(steps, "byte_swap")
    end

    if normalized_format == "signed_integer" or normalized_format == "unsigned_integer" then
        table.insert(steps, "integer_to_float32")
    elseif normalized_format == "float" then
        if bits > 0 and bits ~= 32 then
            table.insert(steps, "float_width_to_float32")
        end
    elseif normalized_format == "companded" then
        table.insert(steps, "decode_to_float32")
    elseif normalized_format ~= "" then
        table.insert(steps, "sample_format_convert")
    end

    if channels > 2 then
        table.insert(steps, "channel_layout_map")
    end

    if #steps > 0 then
        return table.concat(steps, "_")
    end

    if normalized_format == "float" and (bits == 0 or bits == 32) and normalized_order ~= "big_endian" then
        return "already_float32"
    end

    if normalized_format ~= "" then
        return "format_verified"
    end

    return ""
end

local native_mixer_queue_quantum_ms = 10

local function derive_native_mixer_queue_receipt(sample_rate_hz, frame_count)
    local rate = tonumber(sample_rate_hz or 0) or 0
    local frames = tonumber(frame_count or 0) or 0
    if rate <= 0 or frames <= 0 then
        return 0, 0, 0, 0, 0, 0
    end

    local quantum_frames = math.floor(((rate * native_mixer_queue_quantum_ms) / 1000) + 0.5)
    if quantum_frames < 1 then
        quantum_frames = 1
    end

    local queue_depth = math.floor((frames + quantum_frames - 1) / quantum_frames)
    local tail_frames = frames % quantum_frames
    local buffered_ms = queue_depth * native_mixer_queue_quantum_ms
    local tail_ms = 0
    if tail_frames > 0 then
        tail_ms = math.ceil((tail_frames * 1000) / rate)
        if tail_ms < 1 then
            tail_ms = 1
        end
    end

    return native_mixer_queue_quantum_ms,
        clamp_audio_receipt_number(quantum_frames, 2147483647),
        clamp_audio_receipt_number(queue_depth, 4294967295),
        clamp_audio_receipt_number(tail_frames, 2147483647),
        clamp_audio_receipt_number(buffered_ms, 4294967295),
        clamp_audio_receipt_number(tail_ms, 2147483647)
end

local function is_wave_artifact(path, mime_type)
    local normalized_path = trim(path):lower()
    local normalized_mime = mime_type_base(mime_type)

    if normalized_path:sub(-4) == ".wav" then
        return true
    end

    return normalized_mime == "audio/wav"
        or normalized_mime == "audio/wave"
        or normalized_mime == "audio/x-wav"
end

local function read_raw_pcm_audio_format(mime_type, artifact_bytes)
    local normalized_mime = mime_type_base(mime_type)
    local sample_rate_hz = mime_parameter_number(mime_type, { "rate", "sample-rate", "samplerate", "sample_rate" }, 768000)
    local channel_count = mime_parameter_number(mime_type, { "channels", "channel-count", "channel_count" }, 64)
    local bits_per_sample = mime_parameter_number(mime_type, { "bits", "bit-depth", "bit_depth", "bits-per-sample", "bits_per_sample" }, 128)
    local sample_format = normalize_audio_sample_format(mime_parameter_text(mime_type, { "sample-format", "sample_format", "format", "encoding" }))
    local byte_order = normalize_audio_byte_order(mime_parameter_text(mime_type, { "byte-order", "byte_order", "endianness", "endian", "format", "encoding" }))

    if channel_count <= 0 and normalized_mime == "audio/l16" then
        channel_count = 1
    end

    if bits_per_sample <= 0 and normalized_mime == "audio/l16" then
        bits_per_sample = 16
    end

    if normalized_mime == "audio/l16" then
        if sample_format == "" then
            sample_format = "signed_integer"
        end
        if byte_order == "" then
            byte_order = "big_endian"
        end
    end

    local audio_data_bytes = clamp_audio_receipt_number(artifact_bytes, 4294967295)
    local block_align_bytes = 0
    if channel_count > 0 and bits_per_sample > 0 and (bits_per_sample % 8) == 0 then
        block_align_bytes = clamp_audio_receipt_number(channel_count * (bits_per_sample / 8), 65535)
    end

    local byte_rate = 0
    if sample_rate_hz > 0 and block_align_bytes > 0 then
        byte_rate = clamp_audio_receipt_number(sample_rate_hz * block_align_bytes, 4294967295)
    end

    local duration_ms = 0
    if audio_data_bytes > 0 and byte_rate > 0 then
        duration_ms = clamp_audio_receipt_number((audio_data_bytes * 1000) / byte_rate, 86400000)
    end

    return {
        sample_rate_hz = sample_rate_hz,
        channel_count = channel_count,
        bits_per_sample = bits_per_sample,
        duration_ms = duration_ms,
        byte_rate = byte_rate,
        block_align_bytes = block_align_bytes,
        audio_data_bytes = audio_data_bytes,
        valid_bits_per_sample = bits_per_sample,
        channel_mask = 0,
        audio_encoding = normalized_mime == "audio/l16" and "l16_pcm" or "raw_pcm",
        sample_format = sample_format,
        byte_order = byte_order,
    }
end

local function has_wave_header(path)
    local file = io.open(path, "rb")
    if not file then
        return false
    end

    local header = file:read(12) or ""
    file:close()
    return header:sub(1, 4) == "RIFF" and header:sub(9, 12) == "WAVE"
end

local function read_le_u16(value, index)
    local b1, b2 = value:byte(index, index + 1)
    if not b1 or not b2 then
        return nil
    end

    return b1 + (b2 * 256)
end

local function read_le_u32(value, index)
    local b1, b2, b3, b4 = value:byte(index, index + 3)
    if not b1 or not b2 or not b3 or not b4 then
        return nil
    end

    return b1 + (b2 * 256) + (b3 * 65536) + (b4 * 16777216)
end

local wave_subformat_pcm = string.char(0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71)
local wave_subformat_ieee_float = string.char(0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71)

local function describe_wave_audio_encoding(format_tag, fmt)
    if format_tag == 1 then
        return "pcm"
    end

    if format_tag == 3 then
        return "ieee_float"
    end

    if format_tag == 6 then
        return "alaw"
    end

    if format_tag == 7 then
        return "mulaw"
    end

    if format_tag == 65534 then
        local subformat = fmt:sub(25, 40)
        if subformat == wave_subformat_pcm then
            return "extensible_pcm"
        end

        if subformat == wave_subformat_ieee_float then
            return "extensible_ieee_float"
        end

        return "extensible"
    end

    if format_tag and format_tag > 0 then
        return "format_tag_" .. tostring(format_tag)
    end

    return ""
end

local function is_helper_supported_wave_encoding(audio_encoding)
    local normalized = trim(audio_encoding):lower()
    return normalized == ""
        or normalized == "pcm"
        or normalized == "extensible_pcm"
end

local function describe_wave_sample_format(audio_encoding, bits_per_sample)
    local normalized = trim(audio_encoding):lower()
    if normalized == "pcm" or normalized == "extensible_pcm" then
        return bits_per_sample == 8 and "unsigned_integer" or "signed_integer"
    end

    if normalized == "ieee_float" or normalized == "extensible_ieee_float" then
        return "float"
    end

    if normalized == "alaw" or normalized == "mulaw" then
        return "companded"
    end

    return ""
end

local function describe_wave_byte_order(audio_encoding, bits_per_sample)
    local normalized = trim(audio_encoding):lower()
    if bits_per_sample > 8
        and (normalized == "pcm"
            or normalized == "extensible_pcm"
            or normalized == "ieee_float"
            or normalized == "extensible_ieee_float")
    then
        return "little_endian"
    end

    return ""
end

local function read_wave_audio_format(path)
    local file = io.open(path, "rb")
    if not file then
        return nil
    end

    local riff_header = file:read(12) or ""
    if riff_header:sub(1, 4) ~= "RIFF" or riff_header:sub(9, 12) ~= "WAVE" then
        file:close()
        return nil
    end

    local sample_rate_hz = 0
    local channel_count = 0
    local bits_per_sample = 0
    local byte_rate = 0
    local block_align_bytes = 0
    local data_bytes = 0
    local valid_bits_per_sample = 0
    local channel_mask = 0
    local audio_encoding = ""
    local sample_format = ""
    local byte_order = ""

    for _ = 1, 64 do
        local chunk_header = file:read(8) or ""
        if #chunk_header < 8 then
            break
        end

        local chunk_id = chunk_header:sub(1, 4)
        local chunk_size = read_le_u32(chunk_header, 5) or 0
        if chunk_id == "fmt " then
            local fmt_size = math.min(chunk_size, 40)
            local fmt = file:read(fmt_size) or ""
            if #fmt >= 16 then
                local format_tag = read_le_u16(fmt, 1) or 0
                channel_count = read_le_u16(fmt, 3) or 0
                sample_rate_hz = read_le_u32(fmt, 5) or 0
                byte_rate = read_le_u32(fmt, 9) or 0
                block_align_bytes = read_le_u16(fmt, 13) or 0
                bits_per_sample = read_le_u16(fmt, 15) or 0
                audio_encoding = describe_wave_audio_encoding(format_tag, fmt)
                sample_format = describe_wave_sample_format(audio_encoding, bits_per_sample)
                byte_order = describe_wave_byte_order(audio_encoding, bits_per_sample)
                if format_tag == 65534 and #fmt >= 24 then
                    valid_bits_per_sample = read_le_u16(fmt, 19) or 0
                    channel_mask = read_le_u32(fmt, 21) or 0
                end
            end

            local remaining = chunk_size - #fmt
            if remaining > 0 then
                file:seek("cur", remaining)
            end
        elseif chunk_id == "data" then
            data_bytes = chunk_size
            file:seek("cur", chunk_size)
        else
            file:seek("cur", chunk_size)
        end

        if chunk_size % 2 == 1 then
            file:seek("cur", 1)
        end

        if sample_rate_hz > 0 and data_bytes > 0 then
            break
        end
    end

    file:close()

    local duration_ms = 0
    if data_bytes > 0 then
        local bytes_per_second = byte_rate
        if bytes_per_second <= 0 and sample_rate_hz > 0 and channel_count > 0 and bits_per_sample > 0 then
            bytes_per_second = sample_rate_hz * channel_count * (bits_per_sample / 8)
        end

        if bytes_per_second > 0 then
            duration_ms = (data_bytes * 1000) / bytes_per_second
        end
    end

    return {
        sample_rate_hz = clamp_audio_receipt_number(sample_rate_hz, 768000),
        channel_count = clamp_audio_receipt_number(channel_count, 64),
        bits_per_sample = clamp_audio_receipt_number(bits_per_sample, 128),
        duration_ms = clamp_audio_receipt_number(duration_ms, 86400000),
        byte_rate = clamp_audio_receipt_number(byte_rate, 4294967295),
        block_align_bytes = clamp_audio_receipt_number(block_align_bytes, 65535),
        audio_data_bytes = clamp_audio_receipt_number(data_bytes, 4294967295),
        valid_bits_per_sample = clamp_audio_receipt_number(valid_bits_per_sample, 128),
        channel_mask = clamp_audio_receipt_number(channel_mask, 4294967295),
        audio_encoding = audio_encoding,
        sample_format = sample_format,
        byte_order = byte_order,
    }
end

local function is_media_player_artifact(path, mime_type)
    local normalized_path = trim(path):lower()
    local normalized_mime = mime_type_base(mime_type)

    if normalized_path:sub(-4) == ".mp3"
        or normalized_path:sub(-4) == ".m4a"
        or normalized_path:sub(-4) == ".aac"
        or normalized_path:sub(-4) == ".wma"
        or normalized_path:sub(-4) == ".ogg"
        or normalized_path:sub(-5) == ".opus"
        or normalized_path:sub(-5) == ".flac"
    then
        return true
    end

    return normalized_mime == "audio/mpeg"
        or normalized_mime == "audio/mp3"
        or normalized_mime == "audio/mp4"
        or normalized_mime == "audio/x-m4a"
        or normalized_mime == "audio/aac"
        or normalized_mime == "audio/wma"
        or normalized_mime == "audio/x-ms-wma"
        or normalized_mime == "audio/ogg"
        or normalized_mime == "audio/opus"
        or normalized_mime == "audio/flac"
end

local function is_raw_pcm_artifact(path, mime_type)
    local normalized_path = trim(path):lower()
    local normalized_mime = mime_type_base(mime_type)

    return normalized_path:sub(-4) == ".pcm"
        or normalized_mime == "audio/pcm"
        or normalized_mime == "audio/l16"
end

local function to_powershell_literal(value)
    local text = tostring(value or ""):gsub("'", "''")
    return "'" .. text .. "'"
end

local function command_succeeded(ok, why, code)
    if ok == true then
        return true
    end
    if type(ok) == "number" then
        return ok == 0
    end
    return why == "exit" and code == 0
end

local function resolve_speech_playback_mode(path, mime_type, playback_hint)
    local normalized_hint = trim(playback_hint):lower()
    if normalized_hint == "sound_player" or normalized_hint == "media_player" then
        return normalized_hint
    end

    if normalized_hint == "raw_pcm" and is_raw_pcm_artifact(path, mime_type) then
        return "raw_pcm"
    end

    if is_wave_artifact(path, mime_type) then
        return "sound_player"
    end

    if is_media_player_artifact(path, mime_type) then
        return "media_player"
    end

    if is_raw_pcm_artifact(path, mime_type) then
        return "raw_pcm"
    end

    return nil
end

local function build_sound_player_command(normalized_path)
    local script = "& { try { $path = "
        .. to_powershell_literal(normalized_path)
        .. "; if (Test-Path -LiteralPath $path) { "
        .. "$player = New-Object System.Media.SoundPlayer $path; "
        .. "$player.PlaySync() } } catch {} }"
    return 'start "" /min powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "'
        .. script
        .. '"'
end

local function build_media_player_command(normalized_path)
    local script = "& { $player = $null; try { $path = "
        .. to_powershell_literal(normalized_path)
        .. "; if (Test-Path -LiteralPath $path) { "
        .. "$player = New-Object -ComObject WMPlayer.OCX; "
        .. "$player.settings.autoStart = $false; "
        .. "$player.URL = $path; "
        .. "$player.controls.play(); "
        .. "$deadline = (Get-Date).AddSeconds(90); "
        .. "while ((Get-Date) -lt $deadline) { "
        .. "$state = $player.playState; "
        .. "if ($state -eq 1 -or $state -eq 8 -or $state -eq 10) { break } "
        .. "Start-Sleep -Milliseconds 250 } } } catch {} finally { "
        .. "if ($player -ne $null) { "
        .. "try { $player.controls.stop() } catch {} "
        .. "try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($player) } catch {} } } }"
    return 'start "" /min powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "'
        .. script
        .. '"'
end

-- Bounded retry for transient os.execute failures. Windows occasionally refuses
-- a spawn under heavy load (locked file handles, AV scan in progress); one
-- retry with a tiny delay masks the flake without risking a hot loop.
local speech_launch_max_attempts = 2
local speech_launch_retry_delay_ms = 120
local speech_played_recently = {}
local speech_dedupe_ttl_seconds = 10
local speech_playback_sequence = 0
local speech_superseded_total = 0
local speech_last_request_id = ""
local speech_last_started_clock = nil
local speech_last_buffered_ms = 0

local function speech_dedupe_prune(now)
    local cutoff = now - speech_dedupe_ttl_seconds
    for key, at in pairs(speech_played_recently) do
        if at < cutoff then
            speech_played_recently[key] = nil
        end
    end
end

local function elapsed_ms_since(start_clock)
    local elapsed = math.floor(((os.clock() - start_clock) * 1000) + 0.5)
    if elapsed < 0 then
        return 0
    end

    return elapsed
end

local function next_speech_supersession_receipt(request_id)
    speech_playback_sequence = speech_playback_sequence + 1
    local current_request_id = trim(tostring(request_id or ""))
    local superseded_request_id = ""
    local superseded_age_ms = 0
    local superseded_buffered_ms = 0
    local superseded_remaining_ms = 0

    if speech_last_request_id ~= "" and current_request_id ~= "" and speech_last_request_id ~= current_request_id then
        superseded_request_id = speech_last_request_id
        if speech_last_started_clock ~= nil then
            superseded_age_ms = elapsed_ms_since(speech_last_started_clock)
        end
        superseded_buffered_ms = clamp_audio_receipt_number(speech_last_buffered_ms or 0, 4294967295)
        if superseded_buffered_ms > superseded_age_ms then
            superseded_remaining_ms = clamp_audio_receipt_number(superseded_buffered_ms - superseded_age_ms, 4294967295)
        end
        speech_superseded_total = speech_superseded_total + 1
    end

    if current_request_id ~= "" then
        speech_last_request_id = current_request_id
        speech_last_started_clock = os.clock()
    end

    return speech_playback_sequence, superseded_request_id, speech_superseded_total, superseded_age_ms, superseded_buffered_ms, superseded_remaining_ms
end

local function is_native_audio_mixer_mode(playback_mode)
    local normalized = trim(tostring(playback_mode or "")):lower()
    return normalized == "native_mixer"
        or normalized == "native_audio_mixer"
        or normalized == "raw_pcm_native_mixer"
end

local function resolve_speech_cancellation_mode(superseded_request_id, playback_mode)
    if trim(tostring(superseded_request_id or "")) == "" then
        return "none"
    end

    if trim(tostring(playback_mode or "")):lower() == "raw_pcm"
        or is_native_audio_mixer_mode(playback_mode)
    then
        return "native_mixer_pending"
    end

    return "desktop_helper_uncontrolled"
end

local function try_start_native_audio_mixer(
    normalized_path,
    mime_type,
    sample_rate_hz,
    channel_count,
    bits_per_sample,
    duration_ms,
    byte_rate,
    block_align_bytes,
    audio_data_bytes,
    valid_bits_per_sample,
    channel_mask,
    audio_encoding,
    sample_format,
    byte_order,
    mixer_conversion_hint)
    local started_at = os.clock()

    if not native_audio_mixer_enabled then
        return false,
            "speech raw pcm requires native mixer binding",
            "raw_pcm",
            0,
            elapsed_ms_since(started_at),
            "raw_pcm_native_mixer_required"
    end

    local callback_name = trim(native_audio_mixer_callback_name)
    local callback = nil
    if callback_name ~= "" and _G ~= nil then
        callback = _G[callback_name]
    end

    if type(callback) ~= "function" then
        return false,
            "native audio mixer callback unavailable",
            "native_mixer",
            0,
            elapsed_ms_since(started_at),
            "native_audio_mixer_unavailable"
    end

    local ok, result = pcall(callback, {
        path = normalized_path,
        mime_type = mime_type,
        sample_rate_hz = sample_rate_hz,
        channel_count = channel_count,
        bits_per_sample = bits_per_sample,
        duration_ms = duration_ms,
        byte_rate = byte_rate,
        block_align_bytes = block_align_bytes,
        audio_data_bytes = audio_data_bytes,
        valid_bits_per_sample = valid_bits_per_sample,
        channel_mask = channel_mask,
        audio_encoding = audio_encoding,
        sample_format = sample_format,
        byte_order = byte_order,
        mixer_conversion_hint = mixer_conversion_hint,
    })

    if not ok then
        return false,
            "native audio mixer callback failed",
            "native_mixer",
            1,
            elapsed_ms_since(started_at),
            "native_audio_mixer_failed"
    end

    if result == true or (type(result) == "table" and result.started == true) then
        return true,
            "native audio mixer accepted raw pcm",
            "native_mixer",
            1,
            elapsed_ms_since(started_at),
            ""
    end

    return false,
        "native audio mixer rejected raw pcm",
        "native_mixer",
        1,
        elapsed_ms_since(started_at),
        "native_audio_mixer_rejected"
end

local function try_play_speech_file(path, mime_type, playback_hint)
    local started_at = os.clock()
    local normalized_path = trim(decode_escaped_text(path))
    if normalized_path == "" then
        return false, "missing speech path", "", 0, 0, elapsed_ms_since(started_at), "missing_speech_path", 0, 0, 0, 0, 0, 0, 0, 0, 0, ""
    end

    if not file_exists(normalized_path) then
        return false, "speech file missing", "", 0, 0, elapsed_ms_since(started_at), "speech_file_missing", 0, 0, 0, 0, 0, 0, 0, 0, 0, ""
    end

    local artifact_bytes = file_size_bytes(normalized_path)
    if not artifact_bytes then
        return false, "speech file unreadable", "", 0, 0, elapsed_ms_since(started_at), "speech_file_unreadable", 0, 0, 0, 0, 0, 0, 0, 0, 0, ""
    end

    if artifact_bytes <= 0 then
        return false, "speech file empty", "", artifact_bytes, 0, elapsed_ms_since(started_at), "speech_file_empty", 0, 0, 0, 0, 0, 0, 0, 0, 0, ""
    end

    local sample_rate_hz = 0
    local channel_count = 0
    local bits_per_sample = 0
    local duration_ms = 0
    local byte_rate = 0
    local block_align_bytes = 0
    local audio_data_bytes = 0
    local valid_bits_per_sample = 0
    local channel_mask = 0
    local audio_encoding = ""
    local sample_format = ""
    local byte_order = ""
    local mixer_conversion_hint = ""

    if is_wave_artifact(normalized_path, mime_type) then
        if not has_wave_header(normalized_path) then
            return false, "speech wave header invalid", "", artifact_bytes, 0, elapsed_ms_since(started_at), "wave_header_invalid", 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", ""
        end

        local wave_format = read_wave_audio_format(normalized_path)
        if wave_format then
            sample_rate_hz = wave_format.sample_rate_hz or 0
            channel_count = wave_format.channel_count or 0
            bits_per_sample = wave_format.bits_per_sample or 0
            duration_ms = wave_format.duration_ms or 0
            byte_rate = wave_format.byte_rate or 0
            block_align_bytes = wave_format.block_align_bytes or 0
            audio_data_bytes = wave_format.audio_data_bytes or 0
            valid_bits_per_sample = wave_format.valid_bits_per_sample or 0
            channel_mask = wave_format.channel_mask or 0
            audio_encoding = wave_format.audio_encoding or ""
            sample_format = wave_format.sample_format or ""
            byte_order = wave_format.byte_order or ""
        end

        mixer_conversion_hint = describe_native_mixer_conversion_hint(sample_format, byte_order, bits_per_sample, channel_count)

        if not is_helper_supported_wave_encoding(audio_encoding) then
            return false, "speech wave encoding unsupported", "", artifact_bytes, 0, elapsed_ms_since(started_at), "wave_encoding_unsupported", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
        end

        if block_align_bytes > 0 and audio_data_bytes > 0 and (audio_data_bytes % block_align_bytes) ~= 0 then
            return false, "speech wave block alignment invalid", "", artifact_bytes, 0, elapsed_ms_since(started_at), "wave_block_alignment_invalid", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
        end
    elseif is_raw_pcm_artifact(normalized_path, mime_type) then
        local raw_pcm_format = read_raw_pcm_audio_format(mime_type, artifact_bytes)
        sample_rate_hz = raw_pcm_format.sample_rate_hz or 0
        channel_count = raw_pcm_format.channel_count or 0
        bits_per_sample = raw_pcm_format.bits_per_sample or 0
        duration_ms = raw_pcm_format.duration_ms or 0
        byte_rate = raw_pcm_format.byte_rate or 0
        block_align_bytes = raw_pcm_format.block_align_bytes or 0
        audio_data_bytes = raw_pcm_format.audio_data_bytes or 0
        valid_bits_per_sample = raw_pcm_format.valid_bits_per_sample or 0
        channel_mask = raw_pcm_format.channel_mask or 0
        audio_encoding = raw_pcm_format.audio_encoding or ""
        sample_format = raw_pcm_format.sample_format or ""
        byte_order = raw_pcm_format.byte_order or ""
        mixer_conversion_hint = describe_native_mixer_conversion_hint(sample_format, byte_order, bits_per_sample, channel_count)

        if block_align_bytes > 0 and audio_data_bytes > 0 and (audio_data_bytes % block_align_bytes) ~= 0 then
            return false, "speech raw pcm block alignment invalid", "raw_pcm", artifact_bytes, 0, elapsed_ms_since(started_at), "raw_pcm_block_alignment_invalid", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
        end
    end

    local playback_mode = resolve_speech_playback_mode(normalized_path, mime_type, playback_hint)
    if not playback_mode then
        return false, "speech file format unsupported", "", artifact_bytes, 0, elapsed_ms_since(started_at), "unsupported_format", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
    end

    if playback_mode == "raw_pcm" then
        local now = os.time()
        speech_dedupe_prune(now)
        if native_audio_mixer_enabled and speech_played_recently[normalized_path] then
            return false, "duplicate speech within dedupe window", "native_mixer", artifact_bytes, 0, elapsed_ms_since(started_at), "duplicate_within_dedupe_window", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
        end

        local mixer_started, mixer_reason, mixer_mode, mixer_attempt_count, mixer_elapsed_ms, mixer_failure_code =
            try_start_native_audio_mixer(
                normalized_path,
                mime_type,
                sample_rate_hz,
                channel_count,
                bits_per_sample,
                duration_ms,
                byte_rate,
                block_align_bytes,
                audio_data_bytes,
                valid_bits_per_sample,
                channel_mask,
                audio_encoding,
                sample_format,
                byte_order,
                mixer_conversion_hint)
        if mixer_started then
            speech_played_recently[normalized_path] = now
        end
        return mixer_started, mixer_reason, mixer_mode, artifact_bytes, mixer_attempt_count, mixer_elapsed_ms, mixer_failure_code, sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
    end

    local now = os.time()
    speech_dedupe_prune(now)
    if speech_played_recently[normalized_path] then
        return false, "duplicate speech within dedupe window", playback_mode, artifact_bytes, 0, elapsed_ms_since(started_at), "duplicate_within_dedupe_window", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
    end

    local command = playback_mode == "sound_player"
        and build_sound_player_command(normalized_path)
        or build_media_player_command(normalized_path)

    local last_why, last_code
    local attempt_count = 0
    for attempt = 1, speech_launch_max_attempts do
        attempt_count = attempt
        local ok, why, code = os.execute(command)
        if command_succeeded(ok, why, code) then
            speech_played_recently[normalized_path] = now
            return true, playback_mode .. (attempt > 1 and (" after retry " .. tostring(attempt)) or ""), playback_mode, artifact_bytes, attempt_count, elapsed_ms_since(started_at), "", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
        end

        last_why, last_code = why, code
        if attempt < speech_launch_max_attempts then
            -- A tiny delay lets the OS finish whatever blocked the first spawn
            -- (AV scan, explorer.exe contention). Uses busy-wait so we stay on
            -- the UE4SS script thread without depending on LoopAsync.
            local target = os.clock() + (speech_launch_retry_delay_ms / 1000)
            while os.clock() < target do end
        end
    end

    return false, playback_mode .. " launch failed (" .. tostring(last_why or "?") .. ":" .. tostring(last_code or "?") .. ")", playback_mode, artifact_bytes, attempt_count, elapsed_ms_since(started_at), "launch_failed", sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint
end

local function get_player_controller()
    return safe_call(function()
        return FindFirstOf("PlayerController")
    end)
end

-- Phase-4 in-game delivery seam. The runtime emits full presentation plans; the
-- game-side needs to consume them through a real HUD surface. Until a specific
-- Palworld UserWidget is confirmed by the operator via /api/bridge/ui-probe, we
-- keep the richer bind OFF by default and fall through to ClientMessage.
--
-- Operators enable by listing confirmed widget FullNames in
-- native_hud_widget_targets and flipping native_hud_render_enabled to true.
-- Targets are matched in order; the first widget that accepts a SetText call on
-- any reachable TextBlock child wins. Every step is pcall-guarded so a wrong
-- guess degrades cleanly instead of throwing from inside the outbox poll loop.
-- Config is declared near the top of the file so bridge_boot can report the
-- real operator-edited HUD readiness at startup.
local native_hud_text_field_candidates = {
    "MessageText",
    "SubtitleText",
    "BodyText",
    "Text_Message",
    "Text_Subtitle",
    "Text_Body",
}
-- Optional widget fields PalLLM tries to populate with secondary cue data so a
-- bound HUD can show speaker, path badge, subtitle style, and HUD accent without
-- extra per-reply configuration. Missing fields are pcall-skipped.
local native_hud_speaker_field_candidates = {
    "SpeakerText",
    "NameText",
    "Text_Speaker",
    "Text_Name",
}
local native_hud_badge_field_candidates = {
    "BadgeText",
    "PathBadgeText",
    "Text_Badge",
    "Text_Path",
}
local native_hud_accent_field_candidates = {
    "AccentText",
    "HudAccentText",
    "Text_Accent",
    "SubtitleStyleText",
    "Text_Subtitle_Style",
}

-- Module-level stash populated by render_reply right before a card drains. The
-- card-queue path copies cards into a slimmer shape, so we carry per-reply
-- context (speaker / badge / accent) out-of-band instead of threading it
-- through every copy_delivery_card / enqueue_delivery_card call.
local native_hud_latest_context = {
    speaker = "",
    path_badge = "",
    hud_accent = "",
    subtitle_style = "",
}

-- Computes the same badge label as the later build_path_badge helper, but
-- inlined so this module-level stash does not forward-reference a local
-- function defined further down the file. Keep logic in sync with
-- build_path_badge if that function ever grows a new branch.
local function derive_native_hud_path_badge(reply)
    if not reply then
        return ""
    end

    if reply.used_fallback == true then
        return "FALLBACK"
    end

    local response_path = trim(tostring(reply.response_path_raw or "")):lower()
    if response_path:find("bypass", 1, true) then
        return "FAST"
    end
    if response_path:find("live", 1, true) then
        return "LIVE"
    end
    if response_path ~= "" then
        return "SIDECAR"
    end

    return "PAL"
end

local function stash_native_hud_context(reply)
    if not reply then
        return
    end
    native_hud_latest_context = {
        speaker = trim(tostring(reply.speaker or "")),
        path_badge = derive_native_hud_path_badge(reply),
        hud_accent = trim(tostring(reply.presentation
            and reply.presentation.visual
            and reply.presentation.visual.hud_accent or "")),
        subtitle_style = trim(tostring(reply.presentation
            and reply.presentation.audio
            and reply.presentation.audio.subtitle_style or "")),
    }
end

local function set_widget_field_text(widget, candidates, text)
    if trim(tostring(text or "")) == "" then
        return false
    end
    for _, field in ipairs(candidates) do
        local child = safe_call(function() return widget[field] end)
        if child then
            local ok = pcall(function() child:SetText(text) end)
            if ok then
                return true
            end
        end
    end
    return false
end

local function find_widget_by_full_name(full_name)
    local widgets = safe_call(function() return FindAllOf("UserWidget") end)
    if not widgets then
        return nil
    end
    for _, widget in pairs(widgets) do
        local name = safe_call(function() return widget:GetFullName() end)
        if type(name) == "string" and name == full_name then
            return widget
        end
    end
    return nil
end

local function try_set_widget_text(widget, text)
    for _, field in ipairs(native_hud_text_field_candidates) do
        local child = safe_call(function() return widget[field] end)
        if child then
            local ok = pcall(function() child:SetText(text) end)
            if ok then
                return true, field
            end
        end
    end

    -- Fallback: scan the widget's WidgetTree for a TextBlock. This path tolerates
    -- variable naming conventions so an operator doesn't have to enumerate every
    -- possible child field up front.
    local tree = safe_call(function() return widget.WidgetTree end)
    local root = safe_call(function() return tree.RootWidget end)
    if root then
        local scan_ok, scan_result = pcall(function()
            local stack = { root }
            while #stack > 0 do
                local current = table.remove(stack)
                if current then
                    local class_name = safe_call(function() return current:GetClass():GetFullName() end) or ""
                    if type(class_name) == "string" and class_name:find("TextBlock", 1, true) then
                        local ok = pcall(function() current:SetText(text) end)
                        if ok then
                            return "WidgetTreeScan"
                        end
                    end
                    local children = safe_call(function() return current.Slots end)
                    if children then
                        for _, slot in pairs(children) do
                            local content = safe_call(function() return slot.Content end)
                            if content then
                                stack[#stack + 1] = content
                            end
                        end
                    end
                end
            end
            return nil
        end)
        if scan_ok and scan_result then
            return true, scan_result
        end
    end

    return false, nil
end

local function try_native_hud_render(text, duration_seconds)
    if not native_hud_render_enabled or #native_hud_widget_targets == 0 then
        return false, "disabled"
    end

    local context = native_hud_latest_context or {}

    for _, target_name in ipairs(native_hud_widget_targets) do
        local widget = find_widget_by_full_name(target_name)
        if widget then
            local ok, field = try_set_widget_text(widget, text)
            if ok then
                -- Populate the optional richer fields too. Each is a best-effort
                -- pcall and a failure on any secondary field never blocks the
                -- primary render path — the message text is already on-screen.
                set_widget_field_text(widget, native_hud_speaker_field_candidates, context.speaker)
                set_widget_field_text(widget, native_hud_badge_field_candidates, context.path_badge)
                local accent_label = context.hud_accent
                if accent_label == "" then accent_label = context.subtitle_style end
                set_widget_field_text(widget, native_hud_accent_field_candidates, accent_label)

                -- Signal duration upstream so the widget's own blueprint logic can
                -- schedule a dismissal. Duration is advisory; UMG widgets own their
                -- own lifecycle.
                pcall(function() widget:SetRenderOpacity(1.0) end)
                if duration_seconds and duration_seconds > 0 then
                    pcall(function() widget.PalLlmVisibleDurationSeconds = duration_seconds end)
                end
                return true, target_name .. "#" .. tostring(field or "widget")
            end
        end
    end

    return false, "no_widget_match"
end

local function try_screen_message(text, duration_seconds)
    -- Native HUD render first when operators have opted in. Fall through to the
    -- authoritative ClientMessage / PrintString path on any failure so the
    -- message is always delivered.
    local native_ok, native_target = try_native_hud_render(text, duration_seconds)
    if native_ok then
        print("[PalLLM][HudRender] " .. tostring(native_target))
        return true, "native_hud:" .. tostring(native_target)
    end

    local controller = get_player_controller()
    if controller and controller.ClientMessage then
        local ok = pcall(function()
            controller:ClientMessage(text, mod_name, duration_seconds or 6.0)
        end)
        if ok then
            return true, "client_message"
        end
    end

    local kismet = safe_call(function()
        return FindFirstOf("KismetSystemLibrary")
    end)
    if controller and kismet and kismet.PrintString then
        local ok = pcall(function()
            kismet:PrintString(controller, text, true, false, nil, duration_seconds or 6.0)
        end)
        if ok then
            return true, "print_string"
        end
    end

    return false, "render_unavailable"
end

local function get_player_actor()
    local controller = get_player_controller()
    if controller then
        local actor = unwrap_handle(safe_call(function() return controller:GetPawn() end))
            or unwrap_handle(safe_call(function() return controller:K2_GetPawn() end))
            or unwrap_handle(safe_call(function() return controller.Pawn end))
            or unwrap_handle(safe_call(function() return controller.AcknowledgedPawn end))
            or unwrap_handle(safe_call(function() return controller.Character end))
        if actor then
            return actor
        end
    end

    return safe_call(function() return FindFirstOf("PalPlayerCharacter") end)
        or safe_call(function() return FindFirstOf("PalCharacter") end)
end

local function read_actor_location_sample(actor)
    actor = unwrap_handle(actor)
    if actor == nil then
        return nil
    end

    local location = safe_call(function() return actor:GetLocation() end)
    if location == nil then
        location = safe_call(function() return actor:K2_GetActorLocation() end)
    end
    if location == nil then
        return nil
    end

    local x, y, z = read_vector_components(location)
    if x == nil or y == nil or z == nil then
        return nil
    end

    local sector_x = math.floor(x / live_travel_sector_size_units)
    local sector_y = math.floor(y / live_travel_sector_size_units)
    local sample = {
        x = x,
        y = y,
        z = z,
        sector_x = sector_x,
        sector_y = sector_y,
        band = classify_vertical_band(z),
        sampled_at = now_seconds(),
    }
    sample.label = build_sector_label(sample)
    return sample
end

local function distance_2d(a, b)
    local dx = (b.x or 0) - (a.x or 0)
    local dy = (b.y or 0) - (a.y or 0)
    return math.sqrt((dx * dx) + (dy * dy))
end

local function sectors_differ(a, b)
    return a.sector_x ~= b.sector_x
        or a.sector_y ~= b.sector_y
        or a.band ~= b.band
end

local function classify_live_travel_mode(origin, destination, distance_units)
    if origin.band ~= destination.band then
        return "elevation_shift"
    end
    if distance_units >= (live_travel_min_distance_units * 2.4) then
        return "fast_traverse"
    end
    if origin.sector_x ~= destination.sector_x and origin.sector_y ~= destination.sector_y then
        return "cross_country"
    end

    return "foot_patrol"
end

local function emit_live_travel_sample()
    if not live_travel_enabled then
        return
    end

    local current = read_actor_location_sample(get_player_actor())
    if current == nil then
        return
    end

    local anchor = live_travel_state.anchor
    if anchor == nil then
        live_travel_state.anchor = current
        return
    end

    local now = now_seconds()
    if now < (live_travel_state.suppress_until or 0) then
        live_travel_state.anchor = current
        return
    end

    local distance_units = distance_2d(anchor, current)
    if not sectors_differ(anchor, current) then
        if (now - (anchor.sampled_at or now)) >= live_travel_anchor_stale_seconds then
            live_travel_state.anchor = current
        end
        return
    end

    if distance_units < live_travel_min_distance_units then
        return
    end

    if (now - (live_travel_state.last_emitted_at or 0)) < live_travel_min_emit_interval_seconds then
        live_travel_state.anchor = current
        return
    end

    local mode = classify_live_travel_mode(anchor, current, distance_units)
    local note = string.format(
        "live movement sample %.1fk uu from %s to %s",
        distance_units / 1000.0,
        anchor.band,
        current.band)

    write_event("travel", json_object({
        "\"Origin\":" .. quote(anchor.label),
        "\"Destination\":" .. quote(current.label),
        "\"Waypoint\":" .. quote(""),
        "\"Mode\":" .. quote(mode),
        "\"Note\":" .. quote(note),
        "\"RequestId\":" .. quote(""),
        "\"SourceStrategy\":" .. quote("live-movement")
    }))

    print(string.format("[PalLLM] live travel %s -> %s (%.0f uu)", anchor.label, current.label, distance_units))
    live_travel_state.last_emitted_at = now
    live_travel_state.anchor = current
end

LoopAsync(live_travel_poll_interval_ms, function()
    pcall(emit_live_travel_sample)
    return false
end)

local function normalize_action_type(value)
    return trim(decode_escaped_text(value)):lower()
end

local function decode_argument(arguments_body, name, fallback)
    local value = extract_field(arguments_body or "", name)
    local decoded = trim(decode_escaped_text(value or ""))
    if decoded ~= "" then
        return decoded
    end

    return fallback or ""
end

local function build_action_plan(body)
    local action_body = extract_object_body(body, "Action")
    if not action_body then
        return nil
    end

    local raw_type = normalize_action_type(extract_field(action_body, "Type") or "")
    if raw_type == "" then
        return nil
    end

    local arguments_body = extract_object_body(action_body, "Arguments") or ""
    return {
        raw_type = raw_type,
        display_type = humanize_identifier(raw_type),
        priority = extract_integer(action_body, "Priority") or 0,
        justification = trim(decode_escaped_text(extract_field(action_body, "Justification") or "")),
        source_strategy = trim(decode_escaped_text(extract_field(action_body, "SourceStrategy") or "")),
        arguments = {
            reason = decode_argument(arguments_body, "reason"),
            anchor = decode_argument(arguments_body, "anchor"),
            bias = decode_argument(arguments_body, "bias"),
            resource = decode_argument(arguments_body, "resource"),
            origin = decode_argument(arguments_body, "origin"),
            destination = decode_argument(arguments_body, "destination"),
            waypoint = decode_argument(arguments_body, "waypoint"),
            mode = decode_argument(arguments_body, "mode"),
            pal_group = decode_argument(arguments_body, "pal_group"),
            status_change = decode_argument(arguments_body, "status_change"),
            primary_base = decode_argument(arguments_body, "primary_base"),
            secondary_base = decode_argument(arguments_body, "secondary_base"),
            station = decode_argument(arguments_body, "station"),
            item = decode_argument(arguments_body, "item"),
            quantity = decode_argument(arguments_body, "quantity"),
            status = decode_argument(arguments_body, "status"),
        }
    }
end

local function build_presentation_plan(body)
    local presentation_body = extract_object_body(body, "Presentation") or ""
    local audio_body = extract_object_body(presentation_body, "Audio") or ""
    local visual_body = extract_object_body(presentation_body, "Visual") or ""
    local surface_body = extract_object_body(presentation_body, "Surface") or ""

    return {
        source = trim(decode_escaped_text(extract_field(presentation_body, "Source") or "")),
        strategy_id = trim(decode_escaped_text(extract_field(presentation_body, "StrategyId") or "")),
        phase = trim(decode_escaped_text(extract_field(presentation_body, "Phase") or "")),
        summary = trim(decode_escaped_text(extract_field(presentation_body, "Summary") or "")),
        audio = {
            behavior_id = trim(decode_escaped_text(extract_field(audio_body, "BehaviorId") or "")),
            delivery = trim(decode_escaped_text(extract_field(audio_body, "Delivery") or "")),
            voice_print = trim(decode_escaped_text(extract_field(audio_body, "VoicePrint") or "")),
            subtitle_style = trim(decode_escaped_text(extract_field(audio_body, "SubtitleStyle") or "")),
            music_mode = trim(decode_escaped_text(extract_field(audio_body, "MusicMode") or "")),
            stinger = trim(decode_escaped_text(extract_field(audio_body, "Stinger") or "")),
            mix_profile = trim(decode_escaped_text(extract_field(audio_body, "MixProfile") or "")),
            spatialization = trim(decode_escaped_text(extract_field(audio_body, "Spatialization") or "")),
            priority = extract_integer(audio_body, "Priority") or 0,
            cooldown_ms = extract_integer(audio_body, "CooldownMs") or 0,
            layers = extract_string_array(audio_body, "Layers"),
        },
        visual = {
            behavior_id = trim(decode_escaped_text(extract_field(visual_body, "BehaviorId") or "")),
            portrait_expression = trim(decode_escaped_text(extract_field(visual_body, "PortraitExpression") or "")),
            body_pose = trim(decode_escaped_text(extract_field(visual_body, "BodyPose") or "")),
            hud_accent = trim(decode_escaped_text(extract_field(visual_body, "HudAccent") or "")),
            world_marker = trim(decode_escaped_text(extract_field(visual_body, "WorldMarker") or "")),
            screen_treatment = trim(decode_escaped_text(extract_field(visual_body, "ScreenTreatment") or "")),
            camera_treatment = trim(decode_escaped_text(extract_field(visual_body, "CameraTreatment") or "")),
            light_cue = trim(decode_escaped_text(extract_field(visual_body, "LightCue") or "")),
            emote = trim(decode_escaped_text(extract_field(visual_body, "Emote") or "")),
            priority = extract_integer(visual_body, "Priority") or 0,
            hold_ms = extract_integer(visual_body, "HoldMs") or 0,
            layers = extract_string_array(visual_body, "Layers"),
        },
        surface = {
            family_id = trim(decode_escaped_text(extract_field(surface_body, "FamilyId") or "")),
            layout_mode = trim(decode_escaped_text(extract_field(surface_body, "LayoutMode") or "")),
            path_badge = trim(decode_escaped_text(extract_field(surface_body, "PathBadge") or "")),
            family_badge = trim(decode_escaped_text(extract_field(surface_body, "FamilyBadge") or "")),
            phase_badge = trim(decode_escaped_text(extract_field(surface_body, "PhaseBadge") or "")),
            primary_title = trim(decode_escaped_text(extract_field(surface_body, "PrimaryTitle") or "")),
            cue_title = trim(decode_escaped_text(extract_field(surface_body, "CueTitle") or "")),
            readout_title = trim(decode_escaped_text(extract_field(surface_body, "ReadoutTitle") or "")),
            support_title = trim(decode_escaped_text(extract_field(surface_body, "SupportTitle") or "")),
            action_preview_title = trim(decode_escaped_text(extract_field(surface_body, "ActionPreviewTitle") or "")),
            action_feedback_title = trim(decode_escaped_text(extract_field(surface_body, "ActionFeedbackTitle") or "")),
            header_tokens = extract_string_array(surface_body, "HeaderTokens"),
            cue_tokens = extract_string_array(surface_body, "CueTokens"),
            stage_tokens = extract_string_array(surface_body, "StageTokens"),
            atmosphere_tokens = extract_string_array(surface_body, "AtmosphereTokens"),
            focus_tokens = extract_string_array(surface_body, "FocusTokens"),
            status_tokens = extract_string_array(surface_body, "StatusTokens"),
            footer_tokens = extract_string_array(surface_body, "FooterTokens"),
            followup_order = extract_string_array(surface_body, "FollowupOrder"),
            card_budget = extract_integer(surface_body, "CardBudget"),
            primary_cue_token_count = extract_integer(surface_body, "PrimaryCueTokenCount"),
            primary_focus_token_count = extract_integer(surface_body, "PrimaryFocusTokenCount"),
            primary_status_token_count = extract_integer(surface_body, "PrimaryStatusTokenCount"),
            primary_stage_token_count = extract_integer(surface_body, "PrimaryStageTokenCount"),
            primary_atmosphere_token_count = extract_integer(surface_body, "PrimaryAtmosphereTokenCount"),
            width_chars = extract_integer(surface_body, "WidthChars") or 0,
            max_body_lines = extract_integer(surface_body, "MaxBodyLines") or 0,
            primary_duration_ms = extract_integer(surface_body, "PrimaryDurationMs") or 0,
            followup_duration_ms = extract_integer(surface_body, "FollowupDurationMs") or 0,
        },
    }
end

local function build_speech_artifact(body)
    local speech_body = extract_object_body(body, "Speech") or ""
    return {
        request_id = trim(decode_escaped_text(extract_field(speech_body, "RequestId") or "")),
        delivery = trim(decode_escaped_text(extract_field(speech_body, "Delivery") or "")),
        voice = trim(decode_escaped_text(extract_field(speech_body, "Voice") or "")),
        voice_print = trim(decode_escaped_text(extract_field(speech_body, "VoicePrint") or "")),
        subtitle_style = trim(decode_escaped_text(extract_field(speech_body, "SubtitleStyle") or "")),
        mime_type = trim(decode_escaped_text(extract_field(speech_body, "MimeType") or "")),
        playback_hint = trim(decode_escaped_text(extract_field(speech_body, "PlaybackHint") or "")),
        file_path = trim(decode_escaped_text(extract_field(speech_body, "FilePath") or "")),
    }
end

local function build_reply_view(body)
    local presentation = build_presentation_plan(body)
    local speech = build_speech_artifact(body)
    local speaker = decode_escaped_text(extract_field(body, "CharacterName") or "Pal")
    local message = decode_escaped_text(extract_field(body, "AssistantMessage") or "")
    local request_id = trim(decode_escaped_text(extract_field(body, "RequestId") or ""))
    local response_path_raw = take_first_nonempty(
        decode_escaped_text(extract_field(body, "ResponsePath") or ""),
        presentation.source)
    local strategy_raw = take_first_nonempty(
        presentation.strategy_id,
        decode_escaped_text(extract_field(body, "FallbackStrategy") or ""),
        decode_escaped_text(extract_field(body, "StrategyId") or ""))
    local phase_raw = take_first_nonempty(
        presentation.phase,
        decode_escaped_text(extract_field(body, "FallbackPhase") or ""),
        decode_escaped_text(extract_field(body, "Phase") or ""))
    local response_path = humanize_identifier(response_path_raw)
    local strategy = humanize_identifier(strategy_raw)
    local phase = humanize_identifier(phase_raw)
    local summary = trim(decode_escaped_text(take_first_nonempty(
        presentation.summary,
        decode_escaped_text(extract_field(body, "Summary") or ""))))
    local subtitle_style = humanize_identifier(take_first_nonempty(
        presentation.audio.subtitle_style,
        speech.subtitle_style,
        decode_escaped_text(extract_field(body, "SubtitleStyle") or "")))
    local hud_accent = humanize_identifier(take_first_nonempty(
        presentation.visual.hud_accent,
        decode_escaped_text(extract_field(body, "HudAccent") or "")))
    local world_marker = humanize_identifier(take_first_nonempty(
        presentation.visual.world_marker,
        decode_escaped_text(extract_field(body, "WorldMarker") or "")))
    local portrait_expression = humanize_identifier(take_first_nonempty(
        presentation.visual.portrait_expression,
        decode_escaped_text(extract_field(body, "PortraitExpression") or "")))
    local screen_treatment = humanize_identifier(presentation.visual.screen_treatment)
    local camera_treatment = humanize_identifier(presentation.visual.camera_treatment)
    local light_cue = humanize_identifier(presentation.visual.light_cue)
    local voice_print = humanize_identifier(take_first_nonempty(
        speech.voice_print,
        presentation.audio.voice_print,
        decode_escaped_text(extract_field(body, "VoicePrint") or "")))
    local action_plan = build_action_plan(body)
    local used_fallback = extract_boolean(body, "UsedFallback")

    local mode = join_nonempty({
        used_fallback == true and "Fallback" or response_path,
        strategy,
        phase,
    }, " | ")

    local style = join_nonempty({
        subtitle_style ~= "" and ("Subtitle " .. subtitle_style) or "",
        hud_accent ~= "" and ("HUD " .. hud_accent) or "",
        world_marker ~= "" and ("Marker " .. world_marker) or "",
        portrait_expression ~= "" and ("Portrait " .. portrait_expression) or "",
        screen_treatment ~= "" and ("Screen " .. screen_treatment) or "",
        camera_treatment ~= "" and ("Camera " .. camera_treatment) or "",
        light_cue ~= "" and ("Light " .. light_cue) or "",
    }, " | ")

    local action = join_nonempty({
        action_plan and action_plan.display_type or "",
        action_plan and action_plan.justification or "",
    }, " - ")

    return {
        speaker = speaker ~= "" and speaker or "Pal",
        message = message,
        request_id = request_id,
        response_path_raw = response_path_raw,
        strategy_raw = strategy_raw,
        phase_raw = phase_raw,
        used_fallback = used_fallback,
        mode = mode,
        strategy = strategy,
        phase = phase,
        summary = summary,
        style = style,
        action = action,
        action_plan = action_plan,
        presentation = presentation,
        speech = speech,
        speech_path = speech.file_path,
        speech_mime = speech.mime_type,
        speech_playback_hint = speech.playback_hint,
        speech_voice = speech.voice,
        speech_voice_print = voice_print,
    }
end

local function to_positive_int(value, fallback)
    local number = tonumber(trim(value or ""))
    if not number then
        return fallback or 1
    end

    number = math.floor(number)
    if number <= 0 then
        return fallback or 1
    end

    return number
end

local function build_action_trace_note(reply, action_plan, detail)
    return join_nonempty({
        action_plan.justification,
        detail or "",
        reply.request_id ~= "" and ("request " .. reply.request_id) or "",
        action_plan.source_strategy ~= "" and ("strategy " .. action_plan.source_strategy) or "",
    }, " | ")
end

local function prune_delivery_render_keys()
    local now = now_seconds()
    local count = 0
    local oldest_key = nil
    local oldest_seen = nil

    for key, seen_at in pairs(delivery_render_keys) do
        if type(seen_at) ~= "number" or (now - seen_at) >= delivery_dedupe_ttl_seconds then
            delivery_render_keys[key] = nil
        else
            count = count + 1
            if oldest_seen == nil or seen_at < oldest_seen then
                oldest_seen = seen_at
                oldest_key = key
            end
        end
    end

    while count > delivery_dedupe_max_entries and oldest_key ~= nil do
        delivery_render_keys[oldest_key] = nil
        count = count - 1

        oldest_key = nil
        oldest_seen = nil
        for key, seen_at in pairs(delivery_render_keys) do
            if type(seen_at) == "number" and (oldest_seen == nil or seen_at < oldest_seen) then
                oldest_seen = seen_at
                oldest_key = key
            end
        end
    end
end

local function build_delivery_render_key(text, dedupe_key)
    local explicit_key = trim(decode_escaped_text(dedupe_key or "")):lower()
    if explicit_key ~= "" then
        return explicit_key
    end

    local normalized = trim(decode_escaped_text(text or "")):lower()
    normalized = normalized:gsub("%s+", " ")
    if normalized == "" then
        return ""
    end

    return truncate_with_ellipsis(normalized, 160)
end

local function render_delivery_card(card, label)
    if not card or trim(card.text) == "" then
        return false
    end

    local generation = tonumber(card.generation)
    if generation ~= nil and generation ~= delivery_queue_generation then
        return false
    end

    local rendered, surface = try_screen_message(card.text, card.duration)
    if rendered then
        write_event("reply_delivery", json_object({
            "\"RequestId\":" .. quote(tostring(card.request_id or "")),
            "\"Speaker\":" .. quote(tostring(card.speaker or "")),
            "\"ResponsePath\":" .. quote(tostring(card.response_path or "")),
            "\"StrategyId\":" .. quote(tostring(card.strategy or "")),
            "\"Phase\":" .. quote(tostring(card.phase or "")),
            "\"UsedFallback\":" .. ((card.used_fallback == true and "true") or "false"),
            "\"Rendered\":" .. "true",
            "\"Surface\":" .. quote(tostring(surface or "")),
            "\"CardLabel\":" .. quote(tostring(label or "")),
            "\"CardIndex\":" .. tostring(math.max(0, tonumber(card.card_index) or 0)),
            "\"CardCount\":" .. tostring(math.max(0, tonumber(card.card_count) or 0)),
            "\"Note\":" .. quote("visible")
        }))
        return true
    end

    local fallback_text = trim(tostring(card.text or "")):gsub("\r\n", "\n"):gsub("\n", " | ")
    print(string.format("[PalLLM][DeliveryFallback][%s] %s", label or "Card", fallback_text))
    if trim(tostring(card.request_id or "")) ~= "" then
        write_event("reply_delivery", json_object({
            "\"RequestId\":" .. quote(tostring(card.request_id or "")),
            "\"Speaker\":" .. quote(tostring(card.speaker or "")),
            "\"ResponsePath\":" .. quote(tostring(card.response_path or "")),
            "\"StrategyId\":" .. quote(tostring(card.strategy or "")),
            "\"Phase\":" .. quote(tostring(card.phase or "")),
            "\"UsedFallback\":" .. ((card.used_fallback == true and "true") or "false"),
            "\"Rendered\":" .. "false",
            "\"Surface\":" .. quote(tostring(surface or "console_fallback")),
            "\"CardLabel\":" .. quote(tostring(label or "")),
            "\"CardIndex\":" .. tostring(math.max(0, tonumber(card.card_index) or 0)),
            "\"CardCount\":" .. tostring(math.max(0, tonumber(card.card_count) or 0)),
            "\"Note\":" .. quote("not_visible")
        }))
    end
    return false
end

local function copy_delivery_card(card)
    return {
        text = tostring(card and card.text or ""),
        duration = tonumber(card and card.duration) or 4.0,
        priority = tonumber(card and card.priority) or 0,
        request_id = tostring(card and card.request_id or ""),
        speaker = tostring(card and card.speaker or ""),
        response_path = tostring(card and card.response_path or ""),
        strategy = tostring(card and card.strategy or ""),
        phase = tostring(card and card.phase or ""),
        used_fallback = card and card.used_fallback == true or false,
        card_index = tonumber(card and card.card_index) or 0,
        card_count = tonumber(card and card.card_count) or 0,
    }
end

local function annotate_delivery_cards(cards, reply)
    if type(cards) ~= "table" then
        return cards
    end

    local count = #cards
    for index, card in ipairs(cards) do
        if type(card) == "table" then
            card.request_id = reply and reply.request_id or ""
            card.speaker = reply and reply.speaker or ""
            card.response_path = reply and reply.response_path_raw or ""
            card.strategy = reply and reply.strategy_raw or ""
            card.phase = reply and reply.phase_raw or ""
            card.used_fallback = reply and reply.used_fallback == true or false
            card.card_index = index
            card.card_count = count
        end
    end

    return cards
end

local function get_delivery_batch_priority(cards)
    local max_priority = 0
    for _, card in ipairs(cards or {}) do
        local priority = tonumber(card and card.priority) or 0
        if priority > max_priority then
            max_priority = priority
        end
    end

    return max_priority
end

local function should_supersede_delivery_queue(backlog_ms, batch_priority)
    local pressure = tonumber(backlog_ms) or 0
    local priority = tonumber(batch_priority) or 0
    if pressure <= 0 or priority <= 0 then
        return false
    end

    if priority >= 90 and pressure >= delivery_queue_compact_threshold_ms then
        return true
    end
    if priority >= 80 and pressure >= delivery_queue_drop_threshold_ms then
        return true
    end
    if priority >= 70 and pressure >= delivery_queue_collapse_threshold_ms then
        return true
    end

    return false
end

local function resolve_delivery_gap_ms(backlog_ms)
    local pressure = tonumber(backlog_ms) or 0
    if pressure >= delivery_queue_collapse_threshold_ms then
        return 150
    end
    if pressure >= delivery_queue_drop_threshold_ms then
        return 280
    end
    if pressure >= delivery_queue_compact_threshold_ms then
        return 420
    end

    return delivery_channel_gap_ms
end

local function compact_delivery_cards_for_queue_pressure(cards, backlog_ms)
    local compacted = {}
    for _, card in ipairs(cards or {}) do
        if card and trim(card.text) ~= "" then
            compacted[#compacted + 1] = copy_delivery_card(card)
        end
    end

    local pressure = tonumber(backlog_ms) or 0
    local changed = false
    if #compacted == 0 or pressure < delivery_queue_compact_threshold_ms then
        return compacted, changed
    end

    local max_cards = #compacted
    if pressure >= delivery_queue_collapse_threshold_ms then
        max_cards = 1
    elseif pressure >= delivery_queue_drop_threshold_ms then
        max_cards = math.min(2, #compacted)
    end

    while #compacted > max_cards do
        table.remove(compacted)
        changed = true
    end

    local factor = 0.86
    local primary_floor = 3.6
    local followup_floor = 2.5
    if pressure >= delivery_queue_collapse_threshold_ms then
        factor = 0.58
        primary_floor = 3.2
        followup_floor = 1.8
    elseif pressure >= delivery_queue_drop_threshold_ms then
        factor = 0.72
        primary_floor = 3.35
        followup_floor = 2.1
    end

    for index, card in ipairs(compacted) do
        local current = tonumber(card.duration) or 4.0
        local floor = index == 1 and primary_floor or followup_floor
        local scaled = math.max(floor, current * factor)
        if math.abs(scaled - current) > 0.001 then
            changed = true
        end
        card.duration = scaled
    end

    return compacted, changed
end

local function enqueue_delivery_cards(cards, dedupe_key, label)
    if type(cards) ~= "table" or #cards == 0 then
        return false, "empty"
    end

    prune_delivery_render_keys()
    local render_key = build_delivery_render_key(cards[1].text or "", dedupe_key)
    if render_key ~= "" and delivery_render_keys[render_key] then
        return false, "duplicate"
    end
    if render_key ~= "" then
        delivery_render_keys[render_key] = now_seconds()
    end

    local now_ms = now_seconds() * 1000
    if delivery_queue_until_ms < now_ms then
        delivery_queue_until_ms = now_ms
    end

    local backlog_ms = math.max(0, delivery_queue_until_ms - now_ms)
    local batch_priority = get_delivery_batch_priority(cards)
    if should_supersede_delivery_queue(backlog_ms, batch_priority) then
        delivery_queue_generation = delivery_queue_generation + 1
        delivery_queue_until_ms = now_ms
        print(string.format(
            "[PalLLM][DeliveryQueue] supersede backlog=%dms priority=%d generation=%d",
            backlog_ms,
            batch_priority,
            delivery_queue_generation))
        backlog_ms = 0
    end

    local scheduled_cards, queue_compacted = compact_delivery_cards_for_queue_pressure(cards, backlog_ms)
    if queue_compacted then
        print(string.format(
            "[PalLLM][DeliveryQueue] backlog=%dms cards=%d->%d",
            backlog_ms,
            #cards,
            #scheduled_cards))
    end

    local cursor_ms = delivery_queue_until_ms
    local gap_ms = resolve_delivery_gap_ms(backlog_ms)
    local scheduled = false
    for _, card in ipairs(scheduled_cards) do
        if card and trim(card.text) ~= "" then
            local scheduled_card = {
                text = tostring(card.text or ""),
                duration = tonumber(card.duration) or 4.0,
                priority = tonumber(card.priority) or 0,
                generation = delivery_queue_generation,
                request_id = tostring(card.request_id or ""),
                speaker = tostring(card.speaker or ""),
                response_path = tostring(card.response_path or ""),
                strategy = tostring(card.strategy or ""),
                phase = tostring(card.phase or ""),
                used_fallback = card.used_fallback == true,
                card_index = tonumber(card.card_index) or 0,
                card_count = tonumber(card.card_count) or 0,
            }
            local delay_ms = math.max(0, cursor_ms - now_ms)

            if delay_ms == 0 then
                render_delivery_card(scheduled_card, label)
            else
                ExecuteWithDelay(delay_ms, function()
                    pcall(function()
                        render_delivery_card(scheduled_card, label)
                    end)
                end)
            end

            local duration_ms = math.max(1000, math.floor(scheduled_card.duration * 1000))
            cursor_ms = cursor_ms + duration_ms + gap_ms
            scheduled = true
        end
    end

    if scheduled then
        delivery_queue_until_ms = cursor_ms
        return true, "queued"
    end

    return false, "empty"
end

local function enqueue_delivery_card(text, duration_seconds, dedupe_key, label)
    local message = trim(decode_escaped_text(text or ""))
    if message == "" then
        return false, "empty"
    end

    return enqueue_delivery_cards({
        {
            text = message,
            duration = duration_seconds or 4.0,
        }
    }, dedupe_key, label)
end

local function resolve_action_surface_title(reply, key, fallback)
    local surface = reply and reply.presentation and reply.presentation.surface or {}
    local value = trim(tostring(surface[key] or ""))
    if value ~= "" then
        return value
    end

    local support_title = trim(tostring(surface.support_title or ""))
    if support_title ~= "" then
        return support_title
    end

    return fallback
end

local function build_action_delivery_card(reply, title_key, fallback_title, status_label, detail_lines, duration_seconds)
    local surface = reply and reply.presentation and reply.presentation.surface or {}
    local action_plan = reply and reply.action_plan or nil
    local width = tonumber(surface.width_chars) or 54
    if width < 40 then
        width = 40
    elseif width > 64 then
        width = 64
    end

    local max_lines = 6
    local lines = {
        resolve_action_surface_title(reply, title_key, fallback_title),
        truncate_with_ellipsis(join_nonempty({
            trim(surface.path_badge or ""),
            trim(surface.family_badge or ""),
            trim(status_label or ""),
        }, " | "), width),
    }

    for _, detail_line in ipairs(detail_lines or {}) do
        append_prefixed_line(lines, "> ", detail_line, width, max_lines)
    end

    append_prefixed_line(lines, "- ", join_nonempty({
        trim(surface.phase_badge or ""),
        action_plan and action_plan.display_type or "",
        reply and reply.request_id ~= "" and ("Req " .. truncate_with_ellipsis(reply.request_id, 12)) or "",
    }, " | "), width, max_lines)

    local priority = 0
    if action_plan and tonumber(action_plan.priority) then
        priority = tonumber(action_plan.priority) or 0
    end
    priority = math.max(
        priority,
        tonumber(reply and reply.presentation and reply.presentation.audio and reply.presentation.audio.priority) or 0,
        tonumber(reply and reply.presentation and reply.presentation.visual and reply.presentation.visual.priority) or 0)

    return {
        text = table.concat(lines, "\n"),
        duration = duration_seconds or 4.0,
        priority = priority,
    }
end

local function enqueue_action_delivery(reply, title_key, fallback_title, status_label, detail_lines, dedupe_parts, duration_seconds, label)
    local card = build_action_delivery_card(reply, title_key, fallback_title, status_label, detail_lines, duration_seconds)
    annotate_delivery_cards({ card }, reply)
    return enqueue_delivery_cards({
        card,
    }, join_nonempty(dedupe_parts or {}, "|"), label or "Action")
end

-- Queue 3: native map-marker attempt for waypoint_suggest. Safe by construction —
-- no game-state mutation, only a best-effort call into PalMapManager if the
-- current build exposes it. Failure is logged and falls through to the existing
-- bridge-feedback path so automation never gets stuck on a single rename.
local function try_native_waypoint_marker(destination, waypoint)
    if not waypoint_native_marker_enabled then
        return false, "marker_disabled"
    end

    local label = trim(tostring(destination or ""))
    if label == "" then
        return false, "marker_no_destination"
    end

    local map_manager = safe_call(function() return FindFirstOf("PalMapManager") end)
        or safe_call(function() return FindFirstOf("MapManager") end)
    if not map_manager then
        return false, "no_map_manager"
    end

    local ok = pcall(function()
        local hint_label = waypoint ~= "" and (label .. " via " .. waypoint) or label
        if map_manager.AddWaypointHint then
            map_manager:AddWaypointHint(hint_label, mod_name)
        elseif map_manager.SetPlayerWaypointLabel then
            map_manager:SetPlayerWaypointLabel(hint_label)
        elseif map_manager.NotifyWaypointHint then
            map_manager:NotifyWaypointHint(hint_label)
        else
            error("no_compatible_waypoint_api")
        end
    end)

    if ok then
        return true, "map_manager_hint"
    end
    return false, "map_manager_rejected"
end

local function execute_waypoint_suggest(reply, action_plan)
    local args = action_plan.arguments or {}
    local origin = args.origin ~= "" and args.origin or "current_position"
    local destination = args.destination ~= "" and args.destination or (args.resource ~= "" and args.resource or "suggested waypoint")
    local waypoint = args.waypoint ~= "" and args.waypoint or args.bias
    local mode = args.mode ~= "" and args.mode or "guided_route"

    local marker_ok, marker_why = try_native_waypoint_marker(destination, waypoint)
    local marker_line = marker_ok
        and ("Marker " .. humanize_identifier(marker_why))
        or ("Marker skipped (" .. humanize_identifier(marker_why) .. ")")

    enqueue_action_delivery(
        reply,
        "action_preview_title",
        "[Action Preview]",
        "Preview",
        {
            action_plan.display_type,
            join_nonempty({
                "Origin " .. humanize_identifier(origin),
                "Destination " .. humanize_identifier(destination),
            }, " | "),
            join_nonempty({
                waypoint ~= "" and ("Via " .. humanize_identifier(waypoint)) or "",
                "Mode " .. humanize_identifier(mode),
                marker_line,
            }, " | "),
        },
        {
            "action-preview",
            reply.request_id,
            action_plan.raw_type,
            origin,
            destination,
            waypoint,
            mode,
            marker_ok and "marker_ok" or "marker_skipped",
        },
        5.0,
        "ActionPreview")

    if action_executor_dry_run then
        return "dry_run", "route preview only"
    end

    local note = build_action_trace_note(
        reply,
        action_plan,
        marker_ok
            and ("travel feedback emitted | native marker via " .. marker_why)
            or ("travel feedback emitted | native marker skipped: " .. marker_why))
    write_event("travel", json_object({
        "\"Origin\":" .. quote(origin),
        "\"Destination\":" .. quote(destination),
        "\"Waypoint\":" .. quote(waypoint),
        "\"Mode\":" .. quote(mode),
        "\"Note\":" .. quote(note),
        "\"RequestId\":" .. quote(reply.request_id),
        "\"SourceStrategy\":" .. quote(action_plan.source_strategy)
    }))

    return "executed", marker_ok and "native_marker_emitted" or "travel feedback emitted"
end

local function execute_recall_pals(reply, action_plan)
    local args = action_plan.arguments or {}
    local group = args.pal_group ~= "" and args.pal_group or "party"
    local anchor = args.anchor ~= "" and args.anchor or "the main base"
    local status_change = args.status_change ~= "" and args.status_change or "regroup_called"
    local mode = args.mode ~= "" and args.mode or "defensive_regroup"
    enqueue_action_delivery(
        reply,
        "action_preview_title",
        "[Action Preview]",
        "Preview",
        {
            action_plan.display_type,
            humanize_identifier(group) .. " -> " .. humanize_identifier(anchor),
            "Mode " .. humanize_identifier(mode),
        },
        {
            "action-preview",
            reply.request_id,
            action_plan.raw_type,
            group,
            anchor,
            status_change,
            mode,
        },
        5.0,
        "ActionPreview")

    if action_executor_dry_run then
        return "dry_run", "regroup preview only"
    end

    local note = build_action_trace_note(reply, action_plan, "recall feedback emitted via " .. mode)
    write_event("pal_status", json_object({
        "\"PalName\":" .. quote(group),
        "\"Species\":" .. quote(""),
        "\"Change\":" .. quote(status_change),
        "\"Note\":" .. quote(note),
        "\"RequestId\":" .. quote(reply.request_id),
        "\"SourceStrategy\":" .. quote(action_plan.source_strategy)
    }))

    return "executed", "recall feedback emitted"
end

local function execute_craft_queue(reply, action_plan)
    local args = action_plan.arguments or {}
    local primary_base = args.primary_base ~= "" and args.primary_base or "the main base"
    local secondary_base = args.secondary_base ~= "" and args.secondary_base or "the support base"
    local station = args.station ~= "" and args.station or "logistics_planner"
    local item = args.item ~= "" and args.item or "specialization_queue"
    local quantity = to_positive_int(args.quantity, 1)
    local status = args.status ~= "" and args.status or "requested"
    enqueue_action_delivery(
        reply,
        "action_preview_title",
        "[Action Preview]",
        "Preview",
        {
            action_plan.display_type,
            humanize_identifier(primary_base) .. " / " .. humanize_identifier(secondary_base),
            quantity .. "x " .. humanize_identifier(item) .. " @ " .. humanize_identifier(station),
        },
        {
            "action-preview",
            reply.request_id,
            action_plan.raw_type,
            primary_base,
            secondary_base,
            station,
            item,
            tostring(quantity),
            status,
        },
        5.0,
        "ActionPreview")

    if action_executor_dry_run then
        return "dry_run", "queue preview only"
    end

    local note = build_action_trace_note(reply, action_plan, "production feedback emitted")
    write_event("production", json_object({
        "\"BaseId\":" .. quote(primary_base),
        "\"Station\":" .. quote(station),
        "\"Item\":" .. quote(item),
        "\"Quantity\":" .. tostring(quantity),
        "\"Status\":" .. quote(status),
        "\"Note\":" .. quote(note .. " | partner " .. secondary_base),
        "\"RequestId\":" .. quote(reply.request_id),
        "\"SourceStrategy\":" .. quote(action_plan.source_strategy)
    }))

    return "executed", "production feedback emitted"
end

local function prune_executed_action_keys()
    local now = os.time()
    local count = 0
    local oldest_key = nil
    local oldest_seen = nil

    for key, seen_at in pairs(executed_action_keys) do
        if type(seen_at) ~= "number" or (now - seen_at) >= action_executor_dedupe_ttl_seconds then
            executed_action_keys[key] = nil
        else
            count = count + 1
            if oldest_seen == nil or seen_at < oldest_seen then
                oldest_seen = seen_at
                oldest_key = key
            end
        end
    end

    while count > action_executor_dedupe_max_entries and oldest_key ~= nil do
        executed_action_keys[oldest_key] = nil
        count = count - 1

        oldest_key = nil
        oldest_seen = nil
        for key, seen_at in pairs(executed_action_keys) do
            if type(seen_at) == "number" and (oldest_seen == nil or seen_at < oldest_seen) then
                oldest_seen = seen_at
                oldest_key = key
            end
        end
    end
end

local function execute_action_plan(reply)
    local action_plan = reply.action_plan
    if not action_plan or action_plan.raw_type == "" then
        return
    end

    prune_executed_action_keys()
    local request_key = (reply.request_id ~= "" and reply.request_id or "no-request")
        .. ":"
        .. action_plan.raw_type
    if executed_action_keys[request_key] then
        print("[PalLLM][ActionExec] duplicate skipped for " .. request_key)
        return
    end

    if not action_executor_enabled then
        print("[PalLLM][ActionExec] executor disabled for " .. request_key)
        return
    end

    if not action_executor_allowlist[action_plan.raw_type] then
        print("[PalLLM][ActionExec] blocked type " .. action_plan.raw_type)
        return
    end

    local handler = nil
    if action_plan.raw_type == "waypoint_suggest" then
        handler = execute_waypoint_suggest
    elseif action_plan.raw_type == "recall_pals" then
        handler = execute_recall_pals
    elseif action_plan.raw_type == "request_craft_queue" then
        handler = execute_craft_queue
    end

    if not handler then
        print("[PalLLM][ActionExec] no handler for " .. action_plan.raw_type)
        return
    end

    local ok, status, detail = pcall(handler, reply, action_plan)
    if not ok then
        print("[PalLLM][ActionExec] error for " .. request_key .. ": " .. tostring(status))
        return
    end

    executed_action_keys[request_key] = os.time()
    local detail_text = trim(detail)
    print("[PalLLM][ActionExec][" .. request_key .. "] " .. trim(status) .. (detail_text ~= "" and (" | " .. detail_text) or ""))
    enqueue_action_delivery(
        reply,
        "action_feedback_title",
        "[Action Result]",
        humanize_identifier(status),
        {
            action_plan.display_type,
            detail_text,
        },
        {
            "automation",
            request_key,
            status,
            detail_text,
        },
        4.0,
        "Automation")
end

local function classify_strategy_family(strategy_id)
    local normalized = trim(tostring(strategy_id or "")):lower()
    if normalized == "stealth-shadow" then
        return "stealth"
    end
    if normalized == "hero-moment"
        or normalized == "emergency-triage"
        or normalized == "retreat-and-rally"
        or normalized == "nemesis-counterplay"
        or normalized == "buddy-overwatch"
    then
        return "combat"
    end
    if normalized == "perimeter-lockdown"
        or normalized == "base-network"
        or normalized == "crafting-discipline"
    then
        return "base"
    end
    if normalized == "safe-travel"
        or normalized == "objective-push"
        or normalized == "exploration-sweep"
        or normalized == "weather-shelter"
    then
        return "travel"
    end
    if normalized == "capture-window" then
        return "capture"
    end
    if normalized == "ambient-camp" or normalized == "harvest-window" then
        return "camp"
    end
    if normalized == "morale-rally" or normalized == "recover-window" then
        return "recovery"
    end

    return "general"
end

local function build_path_badge(reply)
    local response_path = trim(tostring(reply.response_path_raw or "")):lower()
    if reply.used_fallback == true then
        return "FALLBACK"
    end
    if response_path:find("bypass", 1, true) then
        return "FAST"
    end
    if response_path:find("live", 1, true) then
        return "LIVE"
    end
    if response_path ~= "" then
        return "SIDECAR"
    end

    return "PAL"
end

local function build_family_badge(family)
    if family == "stealth" then
        return "STEALTH"
    end
    if family == "combat" then
        return "ALERT"
    end
    if family == "base" then
        return "BASE"
    end
    if family == "travel" then
        return "ROUTE"
    end
    if family == "capture" then
        return "CAPTURE"
    end
    if family == "camp" then
        return "CAMP"
    end
    if family == "recovery" then
        return "RESET"
    end

    return "GUIDE"
end

local function build_phase_badge(phase)
    local normalized = trim(tostring(phase or "")):lower()
    if normalized == "peak" then
        return "PEAK"
    end
    if normalized == "buildup" or normalized == "build_up" or normalized == "build-up" then
        return "BUILD"
    end
    if normalized == "recover" then
        return "RECOVER"
    end
    if normalized == "relax" then
        return "RELAX"
    end

    return "FLOW"
end

local function build_layout_mode(family)
    if family == "stealth" then
        return "stealth_whisper"
    end
    if family == "combat" then
        return "combat_alert"
    end
    if family == "base" then
        return "operations_panel"
    end
    if family == "travel" then
        return "route_strip"
    end
    if family == "capture" then
        return "capture_focus"
    end
    if family == "camp" then
        return "camp_banner"
    end
    if family == "recovery" then
        return "recovery_breath"
    end

    return "guide_panel"
end

local function build_surface_theme(layout_mode)
    local normalized = trim(tostring(layout_mode or "")):lower()
    if normalized == "stealth_whisper" then
        return {
            title = "[[ STEALTH THREAD ]]",
            cue_title = "[Shadow Cues]",
            support_title = "[Quiet Suggestion]",
            body_prefix = ".. ",
            detail_prefix = ".. ",
            footer_prefix = "shadow ",
        }
    end
    if normalized == "combat_alert" then
        return {
            title = "!! ALERT VECTOR !!",
            cue_title = "[Threat Cues]",
            support_title = "[Immediate Suggestion]",
            body_prefix = "> ",
            detail_prefix = "! ",
            footer_prefix = "front ",
        }
    end
    if normalized == "operations_panel" then
        return {
            title = "== OPERATIONS PANEL ==",
            cue_title = "[Operations Cues]",
            support_title = "[Task Suggestion]",
            body_prefix = "# ",
            detail_prefix = "# ",
            footer_prefix = "ops ",
        }
    end
    if normalized == "route_strip" then
        return {
            title = "--> ROUTE THREAD -->",
            cue_title = "[Route Cues]",
            support_title = "[Route Suggestion]",
            body_prefix = "-> ",
            detail_prefix = "=> ",
            footer_prefix = "route ",
        }
    end
    if normalized == "capture_focus" then
        return {
            title = "<> CAPTURE WINDOW <>",
            cue_title = "[Capture Cues]",
            support_title = "[Capture Suggestion]",
            body_prefix = "* ",
            detail_prefix = "* ",
            footer_prefix = "focus ",
        }
    end
    if normalized == "camp_banner" then
        return {
            title = "~~ CAMP WATCH ~~",
            cue_title = "[Camp Cues]",
            support_title = "[Camp Suggestion]",
            body_prefix = "~ ",
            detail_prefix = "~ ",
            footer_prefix = "camp ",
        }
    end
    if normalized == "recovery_breath" then
        return {
            title = "++ RESET WINDOW ++",
            cue_title = "[Recovery Cues]",
            support_title = "[Recovery Suggestion]",
            body_prefix = "+ ",
            detail_prefix = "+ ",
            footer_prefix = "reset ",
        }
    end

    return {
        title = "[[ FIELD GUIDE ]]",
        cue_title = "[Guide Cues]",
        support_title = "[Guide Suggestion]",
        body_prefix = "- ",
        detail_prefix = "- ",
        footer_prefix = "guide ",
    }
end

local function resolve_surface_title(surface, key, fallback)
    if type(surface) == "table" then
        local value = trim(tostring(surface[key] or ""))
        if value ~= "" then
            return value
        end
    end

    return fallback
end

local function build_followup_title(surface, theme, profile, cue_line, focus_line, status_line, staging_line, treatment_line)
    local has_focus = trim(focus_line) ~= ""
    local has_status = trim(status_line) ~= ""
    local has_cue = trim(cue_line) ~= ""
    local has_scene = trim(staging_line) ~= "" or trim(treatment_line) ~= ""

    if has_focus or has_status then
        return resolve_surface_title(surface, "readout_title", "[Field Readout]")
    end

    if has_cue or has_scene then
        return resolve_surface_title(surface, "cue_title", theme.cue_title)
    end

    return "[Field Note]"
end

local function resolve_bounded_integer(value, fallback, minimum, maximum)
    local number = tonumber(value)
    if not number then
        return fallback
    end

    number = math.floor(number)
    if minimum ~= nil and number < minimum then
        number = minimum
    end
    if maximum ~= nil and number > maximum then
        number = maximum
    end

    return number
end

local function build_default_card_budget(layout_mode, priority, message_length, has_support)
    local budget = priority >= 85 and 3 or 2
    if message_length >= 220 then
        budget = math.min(budget, 2)
    end
    if layout_mode == "stealth_whisper"
        or layout_mode == "route_strip"
        or layout_mode == "recovery_breath"
    then
        budget = math.min(budget, 2)
    end
    if not has_support
        and priority < 45
        and message_length >= 170
        and (layout_mode == "camp_banner" or layout_mode == "guide_panel")
    then
        budget = 1
    end

    return math.max(1, math.min(3, budget))
end

local function build_default_primary_cue_tokens(layout_mode, priority, message_length)
    local count = (layout_mode == "route_strip" or layout_mode == "operations_panel") and 2 or 1
    if priority >= 90 or message_length >= 200 then
        count = math.min(count, 1)
    end

    return math.max(0, math.min(2, count))
end

local function build_default_primary_focus_tokens(layout_mode, message_length)
    local count = (layout_mode == "route_strip"
        or layout_mode == "operations_panel"
        or layout_mode == "combat_alert"
        or layout_mode == "capture_focus")
        and 2 or 1
    if message_length >= 220 then
        count = math.min(count, 1)
    end

    return math.max(0, math.min(2, count))
end

local function build_default_primary_status_tokens(layout_mode, priority, message_length)
    local count = 0
    if layout_mode == "combat_alert"
        or layout_mode == "capture_focus"
        or layout_mode == "recovery_breath"
    then
        count = 2
    elseif layout_mode == "stealth_whisper"
        or layout_mode == "operations_panel"
        or layout_mode == "camp_banner"
    then
        count = 1
    elseif layout_mode == "route_strip" and priority >= 90 then
        count = 1
    end
    if message_length >= 220 then
        count = math.min(count, 1)
    end

    return math.max(0, math.min(2, count))
end

local function build_default_primary_stage_tokens(layout_mode, card_budget)
    if layout_mode == "combat_alert"
        or layout_mode == "capture_focus"
        or layout_mode == "stealth_whisper"
        or layout_mode == "route_strip"
    then
        return 1
    end

    if card_budget <= 1
        and (layout_mode == "operations_panel"
            or layout_mode == "camp_banner"
            or layout_mode == "recovery_breath")
    then
        return 1
    end

    return 0
end

local function build_default_primary_atmosphere_tokens(layout_mode, card_budget, priority)
    if layout_mode == "combat_alert"
        or layout_mode == "capture_focus"
        or layout_mode == "stealth_whisper"
        or layout_mode == "route_strip"
        or layout_mode == "camp_banner"
        or layout_mode == "recovery_breath"
    then
        return 1
    end

    if layout_mode == "operations_panel" and (card_budget <= 1 or (priority or 0) >= 75) then
        return 1
    end

    return 0
end

local function normalize_followup_order(order)
    local normalized = {}
    local seen = {}

    if type(order) == "table" then
        for _, value in ipairs(order) do
            local kind = trim(tostring(value or "")):lower()
            if kind ~= "" and not seen[kind] then
                normalized[#normalized + 1] = kind
                seen[kind] = true
            end
        end
    end

    for _, fallback_kind in ipairs({ "support", "readout", "cue" }) do
        if not seen[fallback_kind] then
            normalized[#normalized + 1] = fallback_kind
        end
    end

    return normalized
end

local function build_default_followup_order(layout_mode, action_priority)
    if (action_priority or 0) >= 80 then
        return { "support", "readout", "cue" }
    end

    if layout_mode == "route_strip"
        or layout_mode == "operations_panel"
        or layout_mode == "recovery_breath"
    then
        return { "readout", "support", "cue" }
    end

    if layout_mode == "combat_alert"
        or layout_mode == "capture_focus"
        or layout_mode == "stealth_whisper"
        or layout_mode == "camp_banner"
    then
        return { "readout", "cue", "support" }
    end

    return { "cue", "readout", "support" }
end

local function build_render_profile(reply)
    local surface = reply.presentation.surface or {}
    local family = trim(surface.family_id) ~= "" and trim(surface.family_id) or classify_strategy_family(reply.strategy_raw)
    local layout_mode = trim(surface.layout_mode) ~= "" and trim(surface.layout_mode) or build_layout_mode(family)
    local audio_priority = reply.presentation.audio.priority or 0
    local visual_priority = reply.presentation.visual.priority or 0
    local action_priority = reply.action_plan and reply.action_plan.priority or 0
    local priority = math.max(audio_priority, visual_priority, action_priority)
    local message_length = #trim(reply.message or "")
    local has_support = reply.action ~= ""
        or (reply.action_plan and reply.action_plan.raw_type ~= "")
        or reply.speech_path ~= ""
    local hold_ms = reply.presentation.visual.hold_ms or 0
    if hold_ms <= 0 then
        hold_ms = 2500
    end

    local message_bonus = math.min(2800, math.max(0, message_length - 64) * 20)
    local primary_duration = math.max(4.5, math.min(9.0, (hold_ms + message_bonus) / 1000.0))
    if priority >= 90 then
        primary_duration = math.max(primary_duration, 6.5)
    end

    local followup_duration = math.max(3.5, math.min(6.0, primary_duration - 1.0))
    local width = 54
    if layout_mode == "stealth_whisper" then
        width = 46
    elseif layout_mode == "combat_alert" or layout_mode == "capture_focus" then
        width = 50
    elseif layout_mode == "operations_panel" or layout_mode == "camp_banner" then
        width = 58
    elseif layout_mode == "route_strip" or layout_mode == "recovery_breath" then
        width = 56
    end
    if (surface.width_chars or 0) > 0 then
        width = surface.width_chars
    end
    if (surface.primary_duration_ms or 0) > 0 then
        primary_duration = math.max(1.0, surface.primary_duration_ms / 1000.0)
    end
    if (surface.followup_duration_ms or 0) > 0 then
        followup_duration = math.max(1.0, surface.followup_duration_ms / 1000.0)
    end
    local max_body_lines = 3
    if layout_mode == "stealth_whisper"
        or layout_mode == "operations_panel"
        or layout_mode == "route_strip"
        or layout_mode == "camp_banner"
        or layout_mode == "recovery_breath"
    then
        max_body_lines = 4
    end
    if (surface.max_body_lines or 0) > 0 then
        max_body_lines = surface.max_body_lines
    end

    local card_budget = resolve_bounded_integer(surface.card_budget, nil, 1, 3)
    if card_budget == nil then
        card_budget = build_default_card_budget(layout_mode, priority, message_length, has_support)
    end

    local primary_cue_tokens = resolve_bounded_integer(surface.primary_cue_token_count, nil, 0, 2)
    if primary_cue_tokens == nil then
        primary_cue_tokens = build_default_primary_cue_tokens(layout_mode, priority, message_length)
    end

    local primary_focus_tokens = resolve_bounded_integer(surface.primary_focus_token_count, nil, 0, 2)
    if primary_focus_tokens == nil then
        primary_focus_tokens = build_default_primary_focus_tokens(layout_mode, message_length)
    end

    local primary_status_tokens = resolve_bounded_integer(surface.primary_status_token_count, nil, 0, 2)
    if primary_status_tokens == nil then
        primary_status_tokens = build_default_primary_status_tokens(layout_mode, priority, message_length)
    end

    local primary_stage_tokens = resolve_bounded_integer(surface.primary_stage_token_count, nil, 0, 1)
    if primary_stage_tokens == nil then
        primary_stage_tokens = build_default_primary_stage_tokens(layout_mode, card_budget)
    end

    local primary_atmosphere_tokens = resolve_bounded_integer(surface.primary_atmosphere_token_count, nil, 0, 1)
    if primary_atmosphere_tokens == nil then
        primary_atmosphere_tokens = build_default_primary_atmosphere_tokens(layout_mode, card_budget, priority)
    end

    local followup_order
    if type(surface.followup_order) == "table" and #surface.followup_order > 0 then
        followup_order = normalize_followup_order(surface.followup_order)
    else
        followup_order = normalize_followup_order(build_default_followup_order(layout_mode, action_priority))
    end

    return {
        family = family,
        layout_mode = layout_mode,
        priority = priority,
        path_badge = trim(surface.path_badge) ~= "" and trim(surface.path_badge) or build_path_badge(reply),
        family_badge = trim(surface.family_badge) ~= "" and trim(surface.family_badge) or build_family_badge(family),
        phase_badge = trim(surface.phase_badge) ~= "" and trim(surface.phase_badge) or build_phase_badge(reply.phase_raw),
        width = width,
        primary_duration = primary_duration,
        followup_duration = followup_duration,
        max_body_lines = max_body_lines,
        card_budget = card_budget,
        primary_cue_tokens = primary_cue_tokens,
        primary_focus_tokens = primary_focus_tokens,
        primary_status_tokens = primary_status_tokens,
        primary_stage_tokens = primary_stage_tokens,
        primary_atmosphere_tokens = primary_atmosphere_tokens,
        followup_order = followup_order,
    }
end

local function build_message_card(reply, profile)
    local theme = build_surface_theme(profile.layout_mode)
    local surface = reply.presentation.surface or {}
    local max_lines = 4
        + profile.max_body_lines
        + 1
        + (profile.primary_status_tokens > 0 and 1 or 0)
        + (profile.primary_stage_tokens > 0 and 1 or 0)
        + (profile.primary_atmosphere_tokens > 0 and 1 or 0)
    local header_line = join_nonempty({
        join_display_tokens(surface.header_tokens, " | ", 3),
        reply.speaker,
    }, " | ")
    if header_line == "" then
        header_line = join_nonempty({
            profile.path_badge,
            profile.family_badge,
            profile.phase_badge,
            reply.speaker,
        }, " | ")
    end

    local lines = {
        resolve_surface_title(surface, "primary_title", theme.title),
        truncate_with_ellipsis(header_line, profile.width),
    }

    local cue_strip = join_display_tokens(surface.cue_tokens, " | ", profile.primary_cue_tokens)
    local focus_strip = join_display_tokens(surface.focus_tokens, " | ", profile.primary_focus_tokens)
    local status_strip = join_display_tokens(surface.status_tokens, " | ", profile.primary_status_tokens)
    local stage_strip = join_display_tokens(surface.stage_tokens, " | ", profile.primary_stage_tokens)
    if profile.primary_stage_tokens > 0 and stage_strip == "" and count_display_tokens(surface.stage_tokens) == 0 then
        stage_strip = join_nonempty({
            reply.presentation.visual.world_marker ~= "" and ("Marker " .. humanize_identifier(reply.presentation.visual.world_marker)) or "",
            reply.presentation.visual.portrait_expression ~= "" and ("Portrait " .. humanize_identifier(reply.presentation.visual.portrait_expression)) or "",
            reply.presentation.visual.body_pose ~= "" and ("Pose " .. humanize_identifier(reply.presentation.visual.body_pose)) or "",
            reply.presentation.visual.emote ~= "" and ("Emote " .. humanize_identifier(reply.presentation.visual.emote)) or "",
        }, " | ")
    end
    local atmosphere_strip = join_display_tokens(surface.atmosphere_tokens, " | ", profile.primary_atmosphere_tokens)
    if profile.primary_atmosphere_tokens > 0 and atmosphere_strip == "" and count_display_tokens(surface.atmosphere_tokens) == 0 then
        atmosphere_strip = join_nonempty({
            reply.presentation.audio.delivery ~= "" and ("Delivery " .. humanize_identifier(reply.presentation.audio.delivery)) or "",
            reply.presentation.audio.voice_print ~= "" and ("Voice " .. humanize_identifier(reply.presentation.audio.voice_print)) or "",
            reply.presentation.audio.music_mode ~= "" and ("Music " .. humanize_identifier(reply.presentation.audio.music_mode)) or "",
            reply.presentation.audio.stinger ~= "" and ("Stinger " .. humanize_identifier(reply.presentation.audio.stinger)) or "",
        }, " | ")
    end
    if profile.layout_mode == "route_strip" then
        append_prefixed_line(lines, theme.detail_prefix, focus_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, status_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, cue_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, stage_strip, profile.width, max_lines)
    elseif profile.layout_mode == "combat_alert"
        or profile.layout_mode == "capture_focus"
        or profile.layout_mode == "recovery_breath"
    then
        append_prefixed_line(lines, theme.detail_prefix, status_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, focus_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, cue_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, stage_strip, profile.width, max_lines)
    elseif profile.layout_mode == "operations_panel" or profile.layout_mode == "camp_banner" then
        append_prefixed_line(lines, theme.detail_prefix, focus_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, status_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, cue_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, stage_strip, profile.width, max_lines)
    else
        append_prefixed_line(lines, theme.detail_prefix, cue_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, focus_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, status_strip, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, stage_strip, profile.width, max_lines)
    end

    append_wrapped_prefixed_lines(lines, reply.message, theme.body_prefix, profile.width, max_lines)
    append_prefixed_line(lines, theme.detail_prefix, atmosphere_strip, profile.width, max_lines)

    local footer = join_display_tokens(surface.footer_tokens, " | ", 3)
    if footer == "" then
        footer = join_nonempty({
            reply.strategy ~= "" and truncate_with_ellipsis(reply.strategy, 18) or "",
            reply.presentation.visual.hud_accent ~= "" and ("HUD " .. truncate_with_ellipsis(humanize_identifier(reply.presentation.visual.hud_accent), 16)) or "",
            reply.presentation.visual.world_marker ~= "" and ("Marker " .. truncate_with_ellipsis(humanize_identifier(reply.presentation.visual.world_marker), 18)) or "",
            reply.presentation.audio.voice_print ~= "" and ("Voice " .. truncate_with_ellipsis(humanize_identifier(reply.presentation.audio.voice_print), 18)) or "",
        }, " | ")
    end

    if footer ~= "" then
        append_prefixed_line(lines, theme.footer_prefix, footer, profile.width, max_lines)
    end

    return {
        text = table.concat(lines, "\n"),
        duration = profile.primary_duration,
        kind = "primary",
        score = 100,
        priority = profile.priority or 0,
    }
end

local function build_cue_card(reply, profile)
    local theme = build_surface_theme(profile.layout_mode)
    local surface = reply.presentation.surface or {}
    local max_lines = 7
    local lines = {}
    local score = 0

    local cue_line = join_display_tokens(
        surface.cue_tokens,
        " | ",
        4,
        profile.primary_cue_tokens)
    if cue_line == "" and count_display_tokens(surface.cue_tokens) == 0 then
        cue_line = join_nonempty({
            reply.presentation.audio.subtitle_style ~= "" and ("Sub " .. humanize_identifier(reply.presentation.audio.subtitle_style)) or "",
            reply.presentation.visual.hud_accent ~= "" and ("HUD " .. humanize_identifier(reply.presentation.visual.hud_accent)) or "",
            reply.presentation.visual.screen_treatment ~= "" and ("Screen " .. humanize_identifier(reply.presentation.visual.screen_treatment)) or "",
        }, " | ")
    end
    if cue_line ~= "" then
        score = score + 1
    end

    local focus_line = join_display_tokens(
        surface.focus_tokens,
        " | ",
        4,
        profile.primary_focus_tokens)
    if focus_line ~= "" then
        score = score + 3
    end

    local status_line = join_display_tokens(
        surface.status_tokens,
        " | ",
        4,
        profile.primary_status_tokens)
    if status_line ~= "" then
        score = score + 2
    end

    local summary_text = take_first_nonempty(reply.summary, reply.presentation.summary)
    if summary_text ~= "" then
        score = score + 2
    end

    local staging_line = join_display_tokens(surface.stage_tokens, " | ", 4, profile.primary_stage_tokens)
    if staging_line == "" and count_display_tokens(surface.stage_tokens) == 0 then
        staging_line = join_nonempty({
            reply.presentation.visual.world_marker ~= "" and ("Marker " .. humanize_identifier(reply.presentation.visual.world_marker)) or "",
            reply.presentation.visual.portrait_expression ~= "" and ("Portrait " .. humanize_identifier(reply.presentation.visual.portrait_expression)) or "",
            reply.presentation.visual.body_pose ~= "" and ("Pose " .. humanize_identifier(reply.presentation.visual.body_pose)) or "",
            reply.presentation.visual.emote ~= "" and ("Emote " .. humanize_identifier(reply.presentation.visual.emote)) or "",
            reply.presentation.visual.camera_treatment ~= "" and ("Camera " .. humanize_identifier(reply.presentation.visual.camera_treatment)) or "",
            reply.presentation.visual.light_cue ~= "" and ("Light " .. humanize_identifier(reply.presentation.visual.light_cue)) or "",
        }, " | ")
    end
    if staging_line ~= "" then
        score = score + 1
    end

    local treatment_line = join_display_tokens(surface.atmosphere_tokens, " | ", 4, profile.primary_atmosphere_tokens)
    if treatment_line == "" and count_display_tokens(surface.atmosphere_tokens) == 0 then
        treatment_line = join_nonempty({
            reply.presentation.audio.delivery ~= "" and ("Delivery " .. humanize_identifier(reply.presentation.audio.delivery)) or "",
            reply.presentation.audio.music_mode ~= "" and ("Music " .. humanize_identifier(reply.presentation.audio.music_mode)) or "",
            reply.presentation.audio.stinger ~= "" and ("Stinger " .. humanize_identifier(reply.presentation.audio.stinger)) or "",
        }, " | ")
    end
    if treatment_line ~= "" then
        score = score + 1
    end

    lines[#lines + 1] = string.format("%s %s", build_followup_title(surface, theme, profile, cue_line, focus_line, status_line, staging_line, treatment_line), profile.family_badge)

    if profile.layout_mode == "combat_alert"
        or profile.layout_mode == "capture_focus"
        or profile.layout_mode == "recovery_breath"
    then
        append_prefixed_line(lines, theme.detail_prefix, focus_line, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, status_line, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, cue_line, profile.width, max_lines)
    elseif profile.layout_mode == "route_strip" or profile.layout_mode == "operations_panel" then
        append_prefixed_line(lines, theme.detail_prefix, focus_line, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, cue_line, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, status_line, profile.width, max_lines)
    else
        append_prefixed_line(lines, theme.detail_prefix, cue_line, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, focus_line, profile.width, max_lines)
        append_prefixed_line(lines, theme.detail_prefix, status_line, profile.width, max_lines)
    end

    append_wrapped_prefixed_lines(lines, summary_text, theme.body_prefix, profile.width, max_lines)
    append_prefixed_line(lines, theme.detail_prefix, staging_line, profile.width, max_lines)
    append_prefixed_line(lines, theme.detail_prefix, treatment_line, profile.width, max_lines)

    if #lines <= 1 then
        return nil
    end

    return {
        text = table.concat(lines, "\n"),
        duration = profile.followup_duration,
        kind = (trim(focus_line) ~= "" or trim(status_line) ~= "") and "readout" or "cue",
        score = score,
        priority = math.max(0, (profile.priority or 0) - 6),
    }
end

local function append_action_support_lines(lines, reply, profile, theme)
    local action_plan = reply.action_plan
    if not action_plan or action_plan.raw_type == "" then
        return
    end

    local args = action_plan.arguments or {}
    local detail_lines = {}
    local max_lines = 5

    if action_plan.raw_type == "waypoint_suggest" then
        detail_lines[#detail_lines + 1] = join_nonempty({
            args.origin ~= "" and ("From " .. humanize_identifier(args.origin)) or "",
            args.destination ~= "" and ("To " .. humanize_identifier(args.destination)) or "",
            args.waypoint ~= "" and ("Via " .. humanize_identifier(args.waypoint)) or "",
        }, " | ")
        detail_lines[#detail_lines + 1] = join_nonempty({
            args.mode ~= "" and ("Mode " .. humanize_identifier(args.mode)) or "",
            args.reason ~= "" and ("Why " .. humanize_identifier(args.reason)) or "",
        }, " | ")
    elseif action_plan.raw_type == "recall_pals" then
        detail_lines[#detail_lines + 1] = join_nonempty({
            args.pal_group ~= "" and humanize_identifier(args.pal_group) or "",
            args.anchor ~= "" and ("Anchor " .. humanize_identifier(args.anchor)) or "",
        }, " | ")
        detail_lines[#detail_lines + 1] = join_nonempty({
            args.mode ~= "" and ("Mode " .. humanize_identifier(args.mode)) or "",
            args.status_change ~= "" and ("Status " .. humanize_identifier(args.status_change)) or "",
        }, " | ")
    elseif action_plan.raw_type == "request_craft_queue" then
        detail_lines[#detail_lines + 1] = join_nonempty({
            args.primary_base ~= "" and humanize_identifier(args.primary_base) or "",
            args.secondary_base ~= "" and ("Support " .. humanize_identifier(args.secondary_base)) or "",
        }, " | ")
        detail_lines[#detail_lines + 1] = join_nonempty({
            args.item ~= "" and (tostring(to_positive_int(args.quantity, 1)) .. "x " .. humanize_identifier(args.item)) or "",
            args.station ~= "" and ("At " .. humanize_identifier(args.station)) or "",
            args.status ~= "" and ("Status " .. humanize_identifier(args.status)) or "",
        }, " | ")
    end

    for _, detail_line in ipairs(detail_lines) do
        if #lines >= max_lines then
            break
        end

        if trim(detail_line) ~= "" then
            append_prefixed_line(lines, theme.detail_prefix, detail_line, profile.width, max_lines)
        end
    end
end

local function will_render_action_delivery(reply)
    local action_plan = reply and reply.action_plan or nil
    if not action_plan or action_plan.raw_type == "" then
        return false
    end

    return action_executor_enabled == true
        and action_executor_allowlist[action_plan.raw_type] == true
end

local function build_support_card(reply, profile)
    local theme = build_surface_theme(profile.layout_mode)
    local surface = reply.presentation.surface or {}
    local max_lines = 5
    local lines = {}
    local has_action_plan = reply.action_plan and reply.action_plan.raw_type ~= ""
    local action_delivery_expected = will_render_action_delivery(reply)
    local should_render_action_support = (reply.action ~= "" or has_action_plan) and not action_delivery_expected

    if should_render_action_support then
        lines[#lines + 1] = resolve_surface_title(surface, "support_title", theme.support_title)
    end

    if reply.action ~= "" and not action_delivery_expected then
        append_wrapped_prefixed_lines(lines, reply.action, theme.body_prefix, profile.width, max_lines)
    end

    if not action_delivery_expected then
        append_action_support_lines(lines, reply, profile, theme)
    end

    local speech_line = join_nonempty({
        reply.speech_path ~= "" and "Speech ready" or "",
        reply.speech_voice_print ~= "" and ("Voiceprint " .. reply.speech_voice_print) or "",
        reply.speech_playback_hint ~= "" and ("Player " .. humanize_identifier(reply.speech_playback_hint)) or "",
        reply.speech_mime ~= "" and reply.speech_mime or "",
    }, " | ")
    if speech_line ~= "" and #lines == 0 then
        lines[#lines + 1] = resolve_surface_title(surface, "support_title", theme.support_title)
    end
    append_prefixed_line(lines, theme.detail_prefix, speech_line, profile.width, max_lines)

    if #lines == 0 then
        return nil
    end

    local score = 0
    if has_action_plan and not action_delivery_expected then
        score = score + 4
    end
    if reply.action ~= "" and not action_delivery_expected then
        score = score + 2
    end
    if reply.speech_path ~= "" then
        score = score + 2
    end
    if reply.speech_voice_print ~= "" or reply.speech_playback_hint ~= "" then
        score = score + 1
    end

    return {
        text = table.concat(lines, "\n"),
        duration = profile.followup_duration,
        kind = "support",
        score = score,
        priority = math.max(0, (profile.priority or 0) - 3),
    }
end

local function compress_render_card_durations(cards, profile)
    if type(cards) ~= "table" or #cards <= 1 then
        return
    end

    local extra_count = #cards - 1
    local primary_floor = profile.layout_mode == "combat_alert" and 4.25 or 3.75
    cards[1].duration = math.max(primary_floor, (tonumber(cards[1].duration) or profile.primary_duration) - (extra_count * 0.35))
    for index = 2, #cards do
        cards[index].duration = math.max(2.5, (tonumber(cards[index].duration) or profile.followup_duration) - (extra_count * 0.45))
    end
end

local function get_followup_kind_rank(profile, kind)
    local order = profile and profile.followup_order or nil
    local target = trim(tostring(kind or "")):lower()
    if type(order) == "table" then
        for index, value in ipairs(order) do
            if trim(tostring(value or "")):lower() == target then
                return index
            end
        end
    end

    return 99
end

local function get_followup_kind_bonus(profile, kind)
    local rank = get_followup_kind_rank(profile, kind)
    if rank == 1 then
        return 2
    end
    if rank == 2 then
        return 1
    end

    return 0
end

local function build_render_cards(reply)
    local profile = build_render_profile(reply)
    local cards = {
        build_message_card(reply, profile),
    }
    local extras = {}

    local cue_card = build_cue_card(reply, profile)
    if cue_card then
        extras[#extras + 1] = cue_card
    end

    local support_card = build_support_card(reply, profile)
    if support_card then
        extras[#extras + 1] = support_card
    end

    table.sort(extras, function(left, right)
        local left_score = left and left.score or 0
        local right_score = right and right.score or 0
        local left_kind = left and left.kind or ""
        local right_kind = right and right.kind or ""
        local left_effective = left_score + get_followup_kind_bonus(profile, left_kind)
        local right_effective = right_score + get_followup_kind_bonus(profile, right_kind)
        if left_effective == right_effective then
            local left_rank = get_followup_kind_rank(profile, left_kind)
            local right_rank = get_followup_kind_rank(profile, right_kind)
            if left_rank ~= right_rank then
                return left_rank < right_rank
            end
            if left_score ~= right_score then
                return left_score > right_score
            end
            if left.kind == right.kind then
                return false
            end
            return tostring(left.kind or "") < tostring(right.kind or "")
        end
        return left_effective > right_effective
    end)

    local max_extra_cards = math.max(0, (profile.card_budget or 2) - 1)
    for _, card in ipairs(extras) do
        if (#cards - 1) >= max_extra_cards then
            break
        end
        cards[#cards + 1] = {
            text = card.text,
            duration = card.duration,
        }
    end

    compress_render_card_durations(cards, profile)

    return cards
end

local function build_reply_delivery_key(reply)
    return join_nonempty({
        "reply",
        reply.request_id,
        reply.response_path_raw,
        reply.strategy,
        reply.message ~= "" and truncate_with_ellipsis(reply.message, 120) or "",
    }, "|")
end

local function try_render_cards(reply, cards)
    return enqueue_delivery_cards(annotate_delivery_cards(cards, reply), build_reply_delivery_key(reply), "Reply")
end

local function render_reply(body)
    local reply = build_reply_view(body)
    if reply.message == "" then return end

    -- Refresh the module-level native HUD stash so try_native_hud_render can
    -- populate speaker / badge / accent widget fields alongside the primary text.
    stash_native_hud_context(reply)

    local cards = build_render_cards(reply)
    local rendered, reason = try_render_cards(reply, cards)
    if not rendered and reason == "empty" then
        local screen_lines = {
            string.format("[%s] %s", reply.speaker, reply.message),
        }
        if reply.mode ~= "" then
            screen_lines[#screen_lines + 1] = reply.mode
        end

        local fallback_card = {
            text = table.concat(screen_lines, "\n"),
            duration = 6.0,
        }
        annotate_delivery_cards({ fallback_card }, reply)
        enqueue_delivery_cards({ fallback_card }, build_reply_delivery_key(reply) .. "|fallback", "ReplyFallback")
    end

    print(string.format("[PalLLM][Reply][%s] %s", reply.speaker, reply.message))
    if reply.request_id ~= "" then
        print("[PalLLM][Request] " .. reply.request_id)
    end
    if reply.mode ~= "" then
        print("[PalLLM][Mode] " .. reply.mode)
    end
    if reply.summary ~= "" then
        print("[PalLLM][Cue] " .. reply.summary)
    end
    if reply.presentation.audio.behavior_id ~= "" then
        print("[PalLLM][AudioCue] " .. join_nonempty({
            humanize_identifier(reply.presentation.audio.behavior_id),
            reply.presentation.audio.subtitle_style ~= "" and ("Subtitle " .. humanize_identifier(reply.presentation.audio.subtitle_style)) or "",
            reply.presentation.audio.voice_print ~= "" and ("Voice " .. humanize_identifier(reply.presentation.audio.voice_print)) or "",
            reply.presentation.audio.music_mode ~= "" and ("Music " .. humanize_identifier(reply.presentation.audio.music_mode)) or "",
        }, " | "))
    end
    if reply.presentation.visual.behavior_id ~= "" then
        print("[PalLLM][VisualCue] " .. join_nonempty({
            humanize_identifier(reply.presentation.visual.behavior_id),
            reply.presentation.visual.hud_accent ~= "" and ("HUD " .. humanize_identifier(reply.presentation.visual.hud_accent)) or "",
            reply.presentation.visual.world_marker ~= "" and ("Marker " .. humanize_identifier(reply.presentation.visual.world_marker)) or "",
            reply.presentation.visual.screen_treatment ~= "" and ("Screen " .. humanize_identifier(reply.presentation.visual.screen_treatment)) or "",
        }, " | "))
    end
    if reply.style ~= "" then
        print("[PalLLM][Style] " .. reply.style)
    end
    local has_action_plan = reply.action_plan and reply.action_plan.raw_type ~= ""
    if reply.action ~= "" or has_action_plan then
        print("[PalLLM][Action] " .. take_first_nonempty(
            reply.action,
            has_action_plan and reply.action_plan.display_type or ""))
    end
    if has_action_plan then
        execute_action_plan(reply)
    end
    if reply.speech_path ~= "" then
        local speech_label = join_nonempty({
            reply.speech_voice_print ~= "" and ("Voiceprint " .. reply.speech_voice_print) or "",
            reply.speech_voice ~= "" and ("Voice " .. reply.speech_voice) or "",
            reply.speech_playback_hint ~= "" and ("Player " .. humanize_identifier(reply.speech_playback_hint)) or "",
            reply.speech_mime ~= "" and reply.speech_mime or "",
        }, " | ")
        print("[PalLLM][Speech] Ready" .. (speech_label ~= "" and (" | " .. speech_label) or ""))

        local playback_sequence, superseded_request_id, superseded_speech_count, superseded_speech_age_ms, superseded_speech_buffered_ms, superseded_speech_remaining_ms =
            next_speech_supersession_receipt(reply.request_id)
        local played, reason, playback_mode, artifact_bytes, attempt_count, elapsed_ms, failure_code, sample_rate_hz, channel_count, bits_per_sample, duration_ms, byte_rate, block_align_bytes, audio_data_bytes, valid_bits_per_sample, channel_mask, audio_encoding, sample_format, byte_order, mixer_conversion_hint = try_play_speech_file(reply.speech_path, reply.speech_mime, reply.speech_playback_hint)
        local cancellation_mode = resolve_speech_cancellation_mode(superseded_request_id, playback_mode)
        local frame_count = 0
        local block_remainder_bytes = 0
        if (block_align_bytes or 0) > 0 and (audio_data_bytes or 0) > 0 then
            frame_count = clamp_audio_receipt_number(math.floor(audio_data_bytes / block_align_bytes), 4294967295)
            block_remainder_bytes = clamp_audio_receipt_number(audio_data_bytes % block_align_bytes, 65535)
        end
        local mixer_quantum_ms, mixer_quantum_frames, mixer_queue_depth_estimate, mixer_tail_frames, mixer_buffered_ms, mixer_tail_ms =
            derive_native_mixer_queue_receipt(sample_rate_hz, frame_count)
        if trim(tostring(reply.request_id or "")) ~= "" then
            speech_last_buffered_ms = clamp_audio_receipt_number(mixer_buffered_ms or 0, 4294967295)
        end
        write_event("speech_playback", json_object({
            "\"RequestId\":" .. quote(reply.request_id),
            "\"Started\":" .. ((played == true and "true") or "false"),
            "\"ArtifactBytes\":" .. tostring(artifact_bytes or 0),
            "\"AttemptCount\":" .. tostring(attempt_count or 0),
            "\"ElapsedMs\":" .. tostring(elapsed_ms or 0),
            "\"PlaybackSequence\":" .. tostring(playback_sequence or 0),
            "\"SupersededRequestId\":" .. quote(superseded_request_id or ""),
            "\"SupersededSpeechCount\":" .. tostring(superseded_speech_count or 0),
            "\"SupersededSpeechAgeMs\":" .. tostring(superseded_speech_age_ms or 0),
            "\"SupersededSpeechBufferedMs\":" .. tostring(superseded_speech_buffered_ms or 0),
            "\"SupersededSpeechRemainingMs\":" .. tostring(superseded_speech_remaining_ms or 0),
            "\"CancellationMode\":" .. quote(cancellation_mode or ""),
            "\"SampleRateHz\":" .. tostring(sample_rate_hz or 0),
            "\"ChannelCount\":" .. tostring(channel_count or 0),
            "\"BitsPerSample\":" .. tostring(bits_per_sample or 0),
            "\"DurationMs\":" .. tostring(duration_ms or 0),
            "\"ByteRate\":" .. tostring(byte_rate or 0),
            "\"BlockAlignBytes\":" .. tostring(block_align_bytes or 0),
            "\"AudioDataBytes\":" .. tostring(audio_data_bytes or 0),
            "\"FrameCount\":" .. tostring(frame_count or 0),
            "\"BlockRemainderBytes\":" .. tostring(block_remainder_bytes or 0),
            "\"ValidBitsPerSample\":" .. tostring(valid_bits_per_sample or 0),
            "\"ChannelMask\":" .. tostring(channel_mask or 0),
            "\"AudioEncoding\":" .. quote(audio_encoding or ""),
            "\"SampleFormat\":" .. quote(sample_format or ""),
            "\"ByteOrder\":" .. quote(byte_order or ""),
            "\"MixerConversionHint\":" .. quote(mixer_conversion_hint or ""),
            "\"MixerQuantumMs\":" .. tostring(mixer_quantum_ms or 0),
            "\"MixerQuantumFrames\":" .. tostring(mixer_quantum_frames or 0),
            "\"MixerQueueDepthEstimate\":" .. tostring(mixer_queue_depth_estimate or 0),
            "\"MixerTailFrames\":" .. tostring(mixer_tail_frames or 0),
            "\"MixerBufferedMs\":" .. tostring(mixer_buffered_ms or 0),
            "\"MixerTailMs\":" .. tostring(mixer_tail_ms or 0),
            "\"PlaybackMode\":" .. quote(playback_mode or ""),
            "\"PlaybackHint\":" .. quote(reply.speech_playback_hint),
            "\"MimeType\":" .. quote(reply.speech_mime),
            "\"FileExtension\":" .. quote(file_extension(reply.speech_path)),
            "\"Reason\":" .. quote(reason),
            "\"FailureCode\":" .. quote(failure_code or "")
        }))
        if played then
            print("[PalLLM][Speech] Playback started via " .. reason .. ": " .. reply.speech_path)
        else
            print("[PalLLM][Speech] Playback skipped: " .. reason)
        end
    end
end

local function drain_outbox_once()
    local files = list_outbox_files()
    for _, path in ipairs(files) do
        local body = read_all(path)
        if body and body ~= "" then
            pcall(render_reply, body)
        end
        -- Move to Archive so the sidecar's health snapshot reflects the drain.
        os.rename(path, join_path(bridge_archive, path:match("([^\\/]+)$") or "chat_reply.json"))
    end
end

-- Poll once a second. The sidecar write cadence is much lower than this so
-- the poll cost is negligible but the perceived latency stays under a frame.
LoopAsync(1000, function()
    pcall(drain_outbox_once)
    return false
end)

-- =====================================================================
-- Periodic screenshot producer
-- =====================================================================
-- The sidecar runs a ScreenshotWatcher background service that consumes PNGs
-- dropped here and feeds them through the vision world-state extractor, which
-- merges into the live snapshot. This closes the loop when a UE4SS hook is
-- unavailable or missed an event. The HighResShot console command is the
-- standard Unreal way to capture the current viewport.

local function emit_screenshot()
    local stamp = os.date("!%Y%m%dT%H%M%SZ")
    local nonce = math.random(100000, 999999)
    local file_name = join_path(bridge_screenshots, string.format("palllm-%s-%d.png", stamp, nonce))
    -- Safe fallback: if the console command is unavailable in the current
    -- UE4SS build, the pcall protects us and we simply skip this tick.
    pcall(function()
        local kismet = FindFirstOf("KismetSystemLibrary")
        if kismet and kismet.ExecuteConsoleCommand then
            local controller = FindFirstOf("PlayerController")
            -- The `HighResShot` command writes into Saved/Screenshots by default; we
            -- follow up with a file move so the PNG lands in our bridge path.
            kismet:ExecuteConsoleCommand(controller, string.format("HighResShot filename=\"%s\" 1", file_name), nil)
        end
    end)
end

LoopAsync(screenshot_interval_ms, function()
    pcall(emit_screenshot)
    return false
end)

-- =====================================================================
-- Production sampler
-- =====================================================================
-- The runtime understands `production` events, but Palworld's crafting loop
-- does not expose a clean single-shot broadcast the way chat/raid/combat do.
-- This sampler polls BaseCampManager on a long interval and only emits when
-- the item+status tuple for a base has actually changed. Defaults to off until
-- operators confirm hook names against their build; when on, the poll cadence
-- is intentionally large and bounded so this cannot create a hot loop.
local production_sampler_interval_ms = 12000
local production_sampler_max_bases_per_poll = 3
local production_state_cache = {}

local function production_sample_key(base_id, station, item, status)
    return table.concat({ base_id or "", station or "", item or "", status or "" }, "|")
end

local function emit_production_sample(base_id, station, item, quantity, status, note)
    local clean_base = trim(base_id or "")
    if clean_base == "" then
        return
    end

    local clean_item = trim(item or "")
    local clean_status = trim(status or "")
    if clean_item == "" and clean_status == "" then
        return
    end

    local cache_key = production_sample_key(clean_base, station, clean_item, clean_status)
    if production_state_cache[clean_base] == cache_key then
        return
    end

    production_state_cache[clean_base] = cache_key
    write_event("production", json_object({
        "\"BaseId\":" .. quote(clean_base),
        "\"Station\":" .. quote(station or ""),
        "\"Item\":" .. quote(clean_item),
        "\"Quantity\":" .. tostring(to_positive_int(quantity, 0)),
        "\"Status\":" .. quote(clean_status),
        "\"Note\":" .. quote(trim(note or "sampler"))
    }))
end

local function sample_base_production(base)
    if not base then
        return
    end

    local base_id = safe_call(function() return tostring(base:GetBaseID()) end)
        or safe_call(function() return tostring(base.BaseId) end)
        or safe_call(function() return tostring(base:GetName()) end)
    if not base_id or base_id == "" then
        return
    end

    -- Prefer the explicit crafting-queue accessor when the build exposes it,
    -- else fall back to the Stations collection. Every lookup is pcall-guarded
    -- so a hook rename degrades to a no-op instead of throwing.
    local current_job = safe_call(function() return base:GetCurrentCraftingJob() end)
        or safe_call(function() return base.CurrentCraftingJob end)
    if current_job then
        local item = safe_call(function() return tostring(current_job:GetItemId()) end)
            or safe_call(function() return tostring(current_job.ItemId) end)
            or ""
        local status = safe_call(function() return tostring(current_job:GetStatus()) end)
            or safe_call(function() return tostring(current_job.Status) end)
            or "crafting"
        local quantity = safe_call(function() return current_job.Quantity end)
            or safe_call(function() return current_job:GetQuantity() end)
            or 0
        local station = safe_call(function() return tostring(current_job:GetStationName()) end)
            or safe_call(function() return tostring(current_job.StationName) end)
            or ""
        emit_production_sample(base_id, station, item, quantity, status, "current_job_sample")
        return
    end

    local stations = safe_call(function() return base.Stations end)
        or safe_call(function() return base:GetStations() end)
    if not stations then
        return
    end

    local emitted = false
    pcall(function()
        for _, station in pairs(stations) do
            if emitted then break end
            local item = safe_call(function() return tostring(station:GetActiveItemId()) end)
                or safe_call(function() return tostring(station.ActiveItemId) end)
                or ""
            local status = safe_call(function() return tostring(station:GetActiveStatus()) end)
                or safe_call(function() return tostring(station.ActiveStatus) end)
                or ""
            if item ~= "" or status ~= "" then
                local station_name = safe_call(function() return tostring(station:GetName()) end) or ""
                local quantity = safe_call(function() return station.ActiveQuantity end) or 0
                emit_production_sample(base_id, station_name, item, quantity, status, "station_sample")
                emitted = true
            end
        end
    end)
end

local function run_production_sampler()
    if not production_sampler_enabled then
        return
    end

    local manager = safe_call(function() return FindFirstOf("PalBaseCampManager") end)
        or safe_call(function() return FindFirstOf("BaseCampManager") end)
    if not manager then
        return
    end

    local bases = safe_call(function() return manager:GetAllBaseCamps() end)
        or safe_call(function() return manager.BaseCamps end)
    if not bases then
        return
    end

    local scanned = 0
    pcall(function()
        for _, base in pairs(bases) do
            if scanned >= production_sampler_max_bases_per_poll then break end
            sample_base_production(base)
            scanned = scanned + 1
        end
    end)
end

LoopAsync(production_sampler_interval_ms, function()
    pcall(run_production_sampler)
    return false
end)
