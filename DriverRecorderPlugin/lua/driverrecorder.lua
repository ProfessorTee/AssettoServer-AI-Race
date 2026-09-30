-- Driver Recorder (AssettoServer DriverRecorderPlugin)
-- Sent by the server to every CSP client. It only records after the player agreed with /rec on in the chat:
-- then it sends the driver's inputs (throttle, brake, clutch, steering, gear) with position and speed, plus tyres and fuel,
-- so the server can build a driving profile ("clone") of this player. /rec off stops it at any time.

local config = ac.configValues({
  sampleHz = 20
})

local BATCH = 5
local recording = false
local sampleHz = math.max(5, math.min(50, tonumber(config.sampleHz) or 20))
local sampleInterval = 1 / sampleHz
local sinceSample = 0
local sinceStatus = 0
local clock = 0
local cleanLaps = 0 -- clean laps saved with this car on this track (from the server)
local bestMs = 0

-- CSP versions differ in which car fields exist: read optional ones safely
local function opt(fn, default)
  local ok, v = pcall(fn)
  if ok and v ~= nil then return v end
  return default
end

local samplesEvent = ac.OnlineEvent({
  ac.StructItem.key('DR_samples'),
  time = ac.StructItem.array(ac.StructItem.float(), BATCH),
  spline = ac.StructItem.array(ac.StructItem.float(), BATCH),
  position = ac.StructItem.array(ac.StructItem.vec3(), BATCH),
  speed = ac.StructItem.array(ac.StructItem.float(), BATCH),
  gas = ac.StructItem.array(ac.StructItem.byte(), BATCH),
  brake = ac.StructItem.array(ac.StructItem.byte(), BATCH),
  clutch = ac.StructItem.array(ac.StructItem.byte(), BATCH),
  steer = ac.StructItem.array(ac.StructItem.int16(), BATCH),
  gear = ac.StructItem.array(ac.StructItem.byte(), BATCH),
  flags = ac.StructItem.array(ac.StructItem.byte(), BATCH)
})

local statusEvent = ac.OnlineEvent({
  ac.StructItem.key('DR_status'),
  tyreTemp = ac.StructItem.array(ac.StructItem.float(), 4),
  tyreWear = ac.StructItem.array(ac.StructItem.float(), 4),
  tyrePressure = ac.StructItem.array(ac.StructItem.float(), 4),
  fuel = ac.StructItem.array(ac.StructItem.float(), 4)
})

local controlEvent = ac.OnlineEvent({
  ac.StructItem.key('DR_control'),
  recording = ac.StructItem.boolean(),
  sampleHz = ac.StructItem.byte(),
  laps = ac.StructItem.uint16(),
  bestMs = ac.StructItem.int32()
}, function(sender, data)
  if sender ~= nil then return end -- only the server may switch it
  local was = recording
  recording = data.recording
  cleanLaps = data.laps or 0
  bestMs = data.bestMs or 0
  if data.sampleHz and data.sampleHz > 0 then
    sampleHz = math.max(5, math.min(50, data.sampleHz))
    sampleInterval = 1 / sampleHz
  end
  if recording and not was then
    ac.setMessage('Driver Recorder', 'Aufzeichnung läuft – /rec off zum Beenden')
  elseif was and not recording then
    ac.setMessage('Driver Recorder', 'Aufzeichnung beendet')
  end
end)

local batch = { n = 0, time = {}, spline = {}, position = {}, speed = {}, gas = {}, brake = {}, clutch = {}, steer = {}, gear = {}, flags = {} }

local function byte01(v)
  return math.max(0, math.min(255, math.floor((v or 0) * 255 + 0.5)))
end

local function addSample(car)
  local n = batch.n + 1
  batch.time[n] = clock
  batch.spline[n] = car.splinePosition
  batch.position[n] = car.position:clone()
  batch.speed[n] = car.speedKmh
  batch.gas[n] = byte01(car.gas)
  batch.brake[n] = byte01(car.brake)
  batch.clutch[n] = byte01(car.clutch)
  batch.steer[n] = math.max(-32000, math.min(32000, math.floor((car.steer or 0) * 10 + 0.5)))
  batch.gear[n] = math.max(0, math.min(20, (car.gear or 0) + 1))
  local f = 0
  if car.isInPitlane then f = f + 1 end
  if opt(function() return car.absInAction end, false) then f = f + 2 end
  if opt(function() return car.tractionControlInAction end, false) then f = f + 4 end
  if opt(function() return car.wheelsOutside end, 0) > 2 then f = f + 8 end
  batch.flags[n] = f
  batch.n = n

  if n >= BATCH then
    samplesEvent({
      time = batch.time, spline = batch.spline, position = batch.position, speed = batch.speed,
      gas = batch.gas, brake = batch.brake, clutch = batch.clutch, steer = batch.steer, gear = batch.gear, flags = batch.flags
    })
    batch.n = 0
  end
end

local function sendStatus(car)
  local temp, wear, pressure = {}, {}, {}
  for i = 0, 3 do
    local w = car.wheels[i]
    temp[i + 1] = opt(function() return w.tyreCoreTemperature end, 0)
    wear[i + 1] = opt(function() return w.tyreWear end, -1)
    pressure[i + 1] = opt(function() return w.tyrePressure end, 0)
  end
  statusEvent({ tyreTemp = temp, tyreWear = wear, tyrePressure = pressure, fuel = { opt(function() return car.fuel end, 0), 0, 0, 0 } })
end

function script.update(dt)
  clock = clock + dt
  if not recording then return end
  local sim = ac.getSim()
  if opt(function() return sim.isReplayActive end, false) or opt(function() return sim.isPaused end, false) then return end
  local car = ac.getCar(0)
  if car == nil then return end

  sinceSample = sinceSample + dt
  if sinceSample >= sampleInterval then
    sinceSample = sinceSample - sampleInterval
    if sinceSample > sampleInterval then sinceSample = 0 end -- after a hitch don't catch up
    addSample(car)
  end

  sinceStatus = sinceStatus + dt
  if sinceStatus >= 1 then
    sinceStatus = 0
    sendStatus(car)
  end
end

local function lapTime(ms)
  local m = math.floor(ms / 60000)
  local s = (ms % 60000) / 1000
  return string.format('%d:%06.3f', m, s)
end

-- small "REC" mark in the top left corner while recording, with the saved clean laps and the best one
function script.drawUI()
  if not recording then return end
  local blink = (clock % 1.5) < 1.1
  local text
  if cleanLaps > 0 then
    text = string.format('REC  %d %s  ·  Beste %s', cleanLaps, cleanLaps == 1 and 'saubere Runde' or 'saubere Runden', lapTime(bestMs))
  else
    text = 'REC  noch keine saubere Runde'
  end
  local width = opt(function() return ui.measureText(text).x end, #text * 7)
  ui.drawRectFilled(vec2(10, 8), vec2(44 + width, 36), rgbm(0, 0, 0, 0.45), 6)
  ui.drawCircleFilled(vec2(22, 22), 6, blink and rgbm(0.95, 0.2, 0.2, 0.9) or rgbm(0.5, 0.1, 0.1, 0.6))
  ui.drawText(text, vec2(34, 14), rgbm(1, 1, 1, 0.85))
end
