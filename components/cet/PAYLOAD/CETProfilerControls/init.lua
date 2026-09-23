local function say(message)
  print("[CETProfilerControls] " .. tostring(message))
end

local function invoke(label, fn, ...)
  if type(fn) ~= "function" then
    say(label .. " unavailable. Install the matching profiler ASI first.")
    return nil, false
  end

  local ok, result = pcall(fn, ...)
  if not ok then
    say(label .. " failed: " .. tostring(result))
    return nil, false
  end

  if result ~= nil then say(result) end
  return result, true
end

local function isRunning()
  if type(CETProfilerIsRunning) ~= "function" then return false end
  local ok, running = pcall(CETProfilerIsRunning)
  return ok and running == true
end

local function toggleCapture()
  if isRunning() then
    local _, pausedOk = invoke("CETProfilerPause", CETProfilerPause)
    if not pausedOk then return end

    local _, dumpOk = invoke("CETProfilerDump", CETProfilerDump)
    if dumpOk then
      say("Capture stopped. CSV results exported automatically.")
    end
    return
  end

  local _, ok = invoke("CETProfilerStart", CETProfilerStart)
  if ok then
    say("Fresh capture started.")
  end
end

registerForEvent("onInit", function()
  say("loaded. One capture input: F11 START / F11 STOP + AUTO EXPORT.")
end)

registerInput("CETProfiler_Toggle", "Profiler: START / STOP + AUTO EXPORT", function(isKeyDown)
  if isKeyDown then toggleCapture() end
end)
