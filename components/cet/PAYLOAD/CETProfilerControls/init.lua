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

local hasCapture = false
local paused = true
local exported = false

local function isRunning()
  if type(CETProfilerIsRunning) ~= "function" then return false end
  local ok, running = pcall(CETProfilerIsRunning)
  return ok and running == true
end

local function toggleCapture()
  if isRunning() then
    local _, ok = invoke("CETProfilerPause", CETProfilerPause)
    if ok then
      hasCapture = true
      paused = true
      exported = false
      say("Capture paused. Press again to resume, or CREATE CSV to finish this measurement.")
    end
    return
  end

  if exported or not hasCapture then
    local _, ok = invoke("CETProfilerStart", CETProfilerStart)
    if ok then
      hasCapture = true
      paused = false
      exported = false
      say("Fresh capture started.")
    end
  else
    local _, ok = invoke("CETProfilerResume", CETProfilerResume)
    if ok then
      paused = false
      say("Capture resumed.")
    end
  end
end

local function createCsv()
  if not hasCapture and not isRunning() then
    say("No measurement has been started yet.")
    return
  end

  -- Finalize the current measurement in a stable paused state. Dump itself does
  -- not reset the native counters; the next START explicitly starts a fresh run.
  if isRunning() then
    local _, pausedOk = invoke("CETProfilerPause", CETProfilerPause)
    if not pausedOk then return end
  end

  local _, dumpOk = invoke("CETProfilerDump", CETProfilerDump)
  if dumpOk then
    paused = true
    exported = true
    say("CSV results created. Use Profiler Manager > Collect Results, then START begins a fresh measurement.")
  end
end

registerForEvent("onInit", function()
  local running = isRunning()
  hasCapture = running
  paused = not running
  exported = false
  say("loaded. Bind two inputs in CET > Bindings.")
  say("START / PAUSE / RESUME never exports. CREATE CSV finalizes and writes the current measurement.")
end)

registerInput("CETProfiler_Toggle", "Profiler: START / PAUSE / RESUME", function(isKeyDown)
  if isKeyDown then toggleCapture() end
end)

registerInput("CETProfiler_Dump", "Profiler: CREATE CSV", function(isKeyDown)
  if isKeyDown then createCsv() end
end)
