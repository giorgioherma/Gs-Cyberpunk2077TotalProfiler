-- Scheduler.lua - deterministic frame/time jobs with pause, spread and elapsed time.

local Scheduler = { version = "0.3.1" }

local frameBuckets = {}
local frameIntervals = {}
local timedJobs = {}
local nextId = 0
local generation = 0
local lastFrame = 0
local Health = nil
local Logger = nil

-- Optional CET native-profiler bridge (v2.11+). If the profiler API is absent,
-- this remains dormant and has no effect on Scheduler behavior.
local Profiler = {
    enabled = false,
    isRunning = nil,
    registerJob = nil,
    frameBegin = nil,
    frameEnd = nil,
    jobBegin = nil,
    jobEnd = nil
}

local function registerProfilerJob(job)
    if not Profiler.enabled or not job or job.profilerHandle then return end

    local handle = Profiler.registerJob(
        job.owner or "unknown",
        job.profilerKind or "unknown",
        job.label or ("job_" .. tostring(job.id or 0)),
        job.profilerInterval or 0,
        job.profilerUnit or ""
    )

    if handle and handle ~= 0 then
        job.profilerHandle = handle
    end
end

local function initProfilerBridge()
    Profiler.enabled =
        type(CETProfilerIsRunning) == "function" and
        type(CETProfilerSchedulerRegisterJob) == "function" and
        type(CETProfilerSchedulerFrameBegin) == "function" and
        type(CETProfilerSchedulerFrameEnd) == "function" and
        type(CETProfilerSchedulerJobBegin) == "function" and
        type(CETProfilerSchedulerJobEnd) == "function"

    if not Profiler.enabled then return end

    Profiler.isRunning = CETProfilerIsRunning
    Profiler.registerJob = CETProfilerSchedulerRegisterJob
    Profiler.frameBegin = CETProfilerSchedulerFrameBegin
    Profiler.frameEnd = CETProfilerSchedulerFrameEnd
    Profiler.jobBegin = CETProfilerSchedulerJobBegin
    Profiler.jobEnd = CETProfilerSchedulerJobEnd

    for _, interval in ipairs(frameIntervals) do
        local bucket = frameBuckets[interval]
        for _, job in ipairs(bucket) do
            registerProfilerJob(job)
        end
    end
    for _, job in ipairs(timedJobs) do
        registerProfilerJob(job)
    end
end

local function noop() end

local function stableHash(text)
    local h = 5381
    text = tostring(text or "")
    for i = 1, #text do
        h = ((h * 33) + string.byte(text, i)) % 2147483647
    end
    return h
end

-- PASS4.2 timed phase allocator.
--
-- PASS4.1 applied a one-time relative offset after a job's first execution.
-- That reduced collisions only when jobs registered at nearly identical times.
-- In practice, different client attach times could cancel those offsets and
-- put 1 s / 5 s maintenance jobs back onto the same rendered frame.
--
-- PASS4.2 assigns repeating timed jobs to absolute phase slots inside their
-- cadence. Jobs sharing a cadence are distributed around the whole interval,
-- then re-align to the same 0-Engine time epoch regardless of registration time.
local timedPhaseBuckets = {}
local timedPhaseAssignments = {}
local phaseGameClock = 0
local phaseRealClock = 0

local function delayKey(delay)
    return string.format("%.6f", delay)
end

local function vanDerCorput2(index)
    local result = 0
    local denom = 1
    local n = index
    while n > 0 do
        denom = denom * 2
        result = result + (n % 2) / denom
        n = math.floor(n / 2)
    end
    return result
end

local function resolveExplicitTimedSpread(delay, repeating, options)
    if not repeating or delay <= 0 or options.spread == false then
        return 0
    end

    -- Preserve PASS4.1 explicit offset/phase semantics as a relative one-time
    -- shift. Automatic jobs use the absolute phase allocator below.
    local explicit = options.offset
    if type(explicit) ~= "number" then
        explicit = options.phase
    end
    if type(explicit) ~= "number" or explicit <= 0 then
        return 0
    end
    return explicit % delay
end

local function resolveAutomaticTimedPhase(delay, repeating, options, owner, label, clock)
    if not repeating or delay <= 0 or options.spread == false or options.catchUp == true then
        return nil
    end

    -- Explicit phase/offset keeps the legacy relative behavior above.
    if type(options.offset) == "number" or type(options.phase) == "number" then
        return nil
    end

    local clockName = clock or "game"
    local bucketKey = clockName .. "|" .. delayKey(delay)
    local identity = bucketKey .. "|" .. tostring(owner) .. "\31" .. tostring(label)

    local assigned = timedPhaseAssignments[identity]
    if assigned ~= nil then
        return assigned
    end

    local bucket = timedPhaseBuckets[bucketKey]
    if not bucket then
        local rotation = (stableHash("phase|" .. bucketKey) % 10000) / 10000
        bucket = { nextSlot = 0, rotation = rotation }
        timedPhaseBuckets[bucketKey] = bucket
    end

    -- 0, 1/2, 1/4, 3/4, 1/8 ... gives good incremental spacing without
    -- needing to know in advance how many clients will register this cadence.
    local fraction = (bucket.rotation + vanDerCorput2(bucket.nextSlot)) % 1
    bucket.nextSlot = bucket.nextSlot + 1

    local phase = fraction * delay
    timedPhaseAssignments[identity] = phase
    return phase
end

local function timeUntilFirstAlignedPhase(delay, phase, clockNow)
    if delay <= 0 or phase == nil then return delay end

    -- One-time alignment only: keep the first ordinary execution unchanged,
    -- then wait at least one requested interval before entering the absolute
    -- phase lane. This may make ONE startup interval longer, never shorter.
    local earliest = clockNow + delay
    local cycles = math.ceil((earliest - phase) / delay - 0.000000001)
    local target = phase + (cycles * delay)
    if target < earliest - 0.000000001 then
        target = target + delay
    end
    return math.max(0, target - clockNow)
end

local function timeUntilNextAbsolutePhase(delay, phase, clockNow)
    if delay <= 0 or phase == nil then return delay end

    -- Once aligned, schedule the immediately following absolute phase slot.
    -- Do NOT add another full delay before searching: doing so skips a slot
    -- whenever the rendered frame has overshot the phase by even a fraction,
    -- which was the PASS4.2 half-frequency bug.
    local cycles = math.floor(((clockNow - phase) / delay) + 0.000000001) + 1
    local target = phase + (cycles * delay)
    local remaining = target - clockNow

    -- Numerical safety only. In normal use target is strictly in the future.
    if remaining <= 0.000000001 then
        remaining = delay
    end
    return remaining
end

local function makeHandle(job)
    local handle = {}
    function handle.Cancel() job.cancelled = true; job.active = false end
    handle.unsubscribe = handle.Cancel
    function handle.Pause() if not job.cancelled then job.active = false end end
    function handle.Resume() if not job.cancelled then job.active = true end end
    function handle.SetActive(value) if not job.cancelled then job.active = value == true end end
    function handle.IsActive() return job.active and not job.cancelled end
    return handle
end

local function safeInvoke(job, ctx, profileActive)
    local token = 0
    if profileActive and job.profilerHandle then
        token = Profiler.jobBegin(job.profilerHandle) or 0
    end

    local ok, err
    if Health then
        ok, err = Health.Invoke(job.owner, "schedule", job.label, job.fn, ctx.frame, ctx)
    else
        ok, err = pcall(job.fn, ctx)
    end

    if token ~= 0 then
        Profiler.jobEnd(job.profilerHandle, token, ctx.frame)
    end

    if not ok and err ~= "quarantined" and Logger then
        Logger.Log("0-Engine", "Scheduled callback error [" .. job.owner .. "/" .. job.label .. "]: " .. tostring(err), "error")
    end
end

function Scheduler.Init(logger, health)
    Logger = logger
    Health = health
    initProfilerBridge()
end

function Scheduler.SetGeneration(value)
    generation = value or (generation + 1)
end

function Scheduler.EveryFrames(interval, options, fn)
    if type(options) == "function" then fn = options; options = {} end
    options = options or {}
    if type(interval) ~= "number" or interval < 1 or type(fn) ~= "function" then
        return { Cancel = noop, unsubscribe = noop, Pause = noop, Resume = noop, IsActive = function() return false end }
    end
    interval = math.floor(interval)
    nextId = nextId + 1
    local offset = options.offset
    if type(offset) ~= "number" then
        offset = options.spread == false and 0 or ((nextId - 1) % interval)
    end
    offset = math.floor(offset) % interval
    local job = {
        id = nextId, owner = options.owner or "unknown",
        label = options.id or options.label or ("frame_" .. nextId),
        fn = fn, interval = interval, offset = offset,
        nextFrame = lastFrame + interval + offset,
        active = options.active ~= false, cancelled = false,
        pausePolicy = options.pause or "when_not_playing",
        accumulated = 0, generation = generation,
        generationScoped = options.generationScoped == true,
        profilerKind = "frame",
        profilerInterval = interval,
        profilerUnit = "frames"
    }
    local bucket = frameBuckets[interval]
    if not bucket then
        bucket = {}
        frameBuckets[interval] = bucket
        frameIntervals[#frameIntervals + 1] = interval
        table.sort(frameIntervals)
    end
    bucket[#bucket + 1] = job
    registerProfilerJob(job)
    return makeHandle(job)
end

function Scheduler.EveryFrame(options, fn)
    return Scheduler.EveryFrames(1, options, fn)
end

local function addTimed(delay, repeating, options, fn)
    if type(options) == "function" then fn = options; options = {} end
    options = options or {}
    if type(delay) ~= "number" or delay < 0 or type(fn) ~= "function" then
        return { Cancel = noop, unsubscribe = noop, Pause = noop, Resume = noop, IsActive = function() return false end }
    end

    nextId = nextId + 1
    local owner = options.owner or "unknown"
    local label = options.id or options.label or (repeating and "interval_" or "timeout_") .. nextId
    local clock = options.clock or "game"
    local spreadOffset = resolveExplicitTimedSpread(delay, repeating, options)
    local absolutePhase = resolveAutomaticTimedPhase(delay, repeating, options, owner, label, clock)

    local job = {
        id = nextId, owner = owner,
        label = label,
        fn = fn, delay = delay, remaining = delay, repeating = repeating,
        active = options.active ~= false, cancelled = false,
        pausePolicy = options.pause or "when_not_playing",
        clock = clock, catchUp = options.catchUp == true,
        maxCatchUp = options.maxCatchUp or 3, elapsed = 0,
        spreadOffset = spreadOffset,
        spreadPending = repeating and spreadOffset > 0,
        absolutePhase = absolutePhase,
        absolutePhaseAligned = false,
        generation = generation, generationScoped = options.generationScoped == true,
        profilerKind = repeating and "timed" or "oneshot",
        profilerInterval = delay,
        profilerUnit = "seconds"
    }
    timedJobs[#timedJobs + 1] = job
    registerProfilerJob(job)
    return makeHandle(job)
end

function Scheduler.Every(seconds, options, fn) return addTimed(seconds, true, options, fn) end
function Scheduler.After(seconds, options, fn) return addTimed(seconds, false, options, fn) end
function Scheduler.NextTick(options, fn)
    return addTimed(0, false, options, fn)
end

local function shouldRun(job, playing)
    if not job.active or job.cancelled then return false end
    if job.generationScoped and job.generation ~= generation then job.cancelled = true; return false end
    if job.pausePolicy == "when_not_playing" and not playing then return false end
    return true
end

function Scheduler.Update(ctx)
    lastFrame = ctx.frame

    local profileActive = Profiler.enabled and Profiler.isRunning()
    if profileActive then
        Profiler.frameBegin(ctx.frame)
    end

    local gameNow = tonumber(ctx.now)
    if gameNow ~= nil then
        phaseGameClock = gameNow
    else
        phaseGameClock = phaseGameClock + (ctx.dt or 0)
    end
    phaseRealClock = phaseRealClock + (ctx.realDt or ctx.dt or 0)

    for _, interval in ipairs(frameIntervals) do
        local bucket = frameBuckets[interval]
        local dirty = false
        for i = 1, #bucket do
            local job = bucket[i]
            if job and not job.cancelled then
                local runnable = shouldRun(job, ctx.playing)
                if runnable then job.accumulated = job.accumulated + ctx.dt end
                if runnable and ctx.frame >= job.nextFrame then
                    local jobCtx = {
                        frame = ctx.frame, dt = ctx.dt, realDt = ctx.realDt, elapsed = job.accumulated,
                        now = ctx.now, generation = ctx.generation,
                        playing = ctx.playing, inMenu = ctx.inMenu,
                        player = ctx.player, state = ctx.state
                    }
                    job.accumulated = 0
                    job.nextFrame = ctx.frame + interval
                    safeInvoke(job, jobCtx, profileActive)
                end
            else dirty = true end
        end
        if dirty then
            local write = 0
            for read = 1, #bucket do
                local job = bucket[read]
                if job and not job.cancelled then write = write + 1; bucket[write] = job end
            end
            for i = write + 1, #bucket do bucket[i] = nil end
        end
    end

    local dirty = false
    for i = 1, #timedJobs do
        local job = timedJobs[i]
        if job and not job.cancelled then
            local step = job.clock == "real" and (ctx.realDt or ctx.dt) or ctx.dt
            local advances = job.active and (job.clock == "real" or ctx.playing or job.pausePolicy == "never")
            if advances then
                job.remaining = job.remaining - step
                job.elapsed = job.elapsed + step
            end
            if shouldRun(job, ctx.playing) and job.remaining <= 0 then
                local runs = 1
                if job.catchUp and job.delay > 0 then
                    runs = math.min(job.maxCatchUp, 1 + math.floor((-job.remaining) / job.delay))
                end
                for _ = 1, runs do
                    local jobCtx = {
                        frame = ctx.frame, dt = ctx.dt, realDt = ctx.realDt, elapsed = job.elapsed,
                        now = ctx.now, generation = ctx.generation,
                        playing = ctx.playing, inMenu = ctx.inMenu,
                        player = ctx.player, state = ctx.state
                    }
                    safeInvoke(job, jobCtx, profileActive)
                    if job.cancelled then break end
                end
                job.elapsed = 0
                if job.repeating and not job.cancelled then
                    if job.absolutePhase ~= nil and not job.catchUp then
                        local phaseClock = job.clock == "real" and phaseRealClock or phaseGameClock
                        if not job.absolutePhaseAligned then
                            job.remaining = timeUntilFirstAlignedPhase(job.delay, job.absolutePhase, phaseClock)
                            job.absolutePhaseAligned = true
                        else
                            job.remaining = timeUntilNextAbsolutePhase(job.delay, job.absolutePhase, phaseClock)
                        end
                    else
                        local firstSpread = 0
                        if job.spreadPending then
                            firstSpread = job.spreadOffset or 0
                            job.spreadPending = false
                        end

                        if job.catchUp then
                            job.remaining = job.remaining + (job.delay * runs) + firstSpread
                        else
                            job.remaining = job.delay + firstSpread
                        end
                    end
                else
                    job.cancelled = true
                    job.active = false
                end
            end
        else dirty = true end
    end
    if dirty then
        local write = 0
        for read = 1, #timedJobs do
            local job = timedJobs[read]
            if job and not job.cancelled then write = write + 1; timedJobs[write] = job end
        end
        for i = write + 1, #timedJobs do timedJobs[i] = nil end
    end
    if profileActive then
        Profiler.frameEnd(ctx.frame)
    end

end

function Scheduler.GetInfo()
    local frames, timers, active, phased = 0, 0, 0, 0
    for _, interval in ipairs(frameIntervals) do
        local bucket = frameBuckets[interval]
        for _, job in ipairs(bucket) do
            if not job.cancelled then frames = frames + 1; if job.active then active = active + 1 end end
        end
    end
    for _, job in ipairs(timedJobs) do
        if not job.cancelled then
            timers = timers + 1
            if job.active then active = active + 1 end
            if job.absolutePhase ~= nil or (job.spreadOffset or 0) > 0 then phased = phased + 1 end
        end
    end
    return {
        frameJobs = frames,
        timedJobs = timers,
        activeJobs = active,
        staggeredTimedJobs = phased,
        absolutePhasedTimedJobs = phased
    }
end

return Scheduler
