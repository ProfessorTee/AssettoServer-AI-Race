-- Race AI driver swap (sent by the server to CSP clients)
-- While a player's AI clone drives his car, the player can watch from a spare car in the pits.
-- The server tells this script what's going on (banner), points the camera at the own car if CSP allows it,
-- and reconnects the player into his own car when the clone is waiting for him in the pit box.

local state = { phase = 0, car = 255, position = 0, eta = 0, at = 0 }
local clock = 0
local reconnectAt = -1
local reconnectModel = ''
local focused = false

local function opt(fn, default)
  local ok, v = pcall(fn)
  if ok and v ~= nil then return v end
  return default
end

-- phase: 0 = nothing, 1 = your clone drives (/play brings it to the pits), 2 = clone comes to the pits,
--        3 = reconnecting you now (driver change), 4 = go to your pit box, the clone takes over there,
--        5 = you're back while your clone drives: moving you to a spare car to watch,
--        6 = track change: the server restarts with the next track, you're reconnected after eta seconds
ac.OnlineEvent({
  ac.StructItem.key('RAI_swap'),
  phase = ac.StructItem.byte(),
  car = ac.StructItem.byte(),
  position = ac.StructItem.byte(),
  eta = ac.StructItem.uint16(),
  reconnect = ac.StructItem.byte(),
  model = ac.StructItem.string(64),
  info = ac.StructItem.string(48)
}, function(sender, data)
  if sender ~= nil then return end
  local was = state.phase
  state.phase = data.phase
  state.car = data.car
  state.position = data.position
  state.eta = data.eta
  state.at = clock
  state.info = data.info
  if data.reconnect > 0 and reconnectAt < 0 then
    reconnectAt = clock + data.reconnect
    reconnectModel = data.model
  end
  if state.phase == 0 then focused = false end
  if state.phase ~= was and state.phase == 3 then
    ac.setMessage('Fahrerwechsel', 'Du wirst gleich umgesetzt')
  end
end)

local function ownCarIndex()
  for i, car in ac.iterateCars() do
    if opt(function() return car.sessionID end, -1) == state.car then return car.index end
  end
  return nil
end

function script.update(dt)
  clock = clock + dt
  if reconnectAt >= 0 and clock >= reconnectAt then
    reconnectAt = -1
    local model = (reconnectModel ~= nil and reconnectModel ~= '') and reconnectModel or ac.getCarID(0)
    ac.reconnectTo({ carID = model })
  end
  -- watch the own car driven by the clone (if this CSP version allows focusing other cars online)
  if (state.phase == 1 or state.phase == 2) and not focused and ac.focusCar ~= nil then
    local idx = ownCarIndex()
    if idx ~= nil then focused = opt(function() return ac.focusCar(idx) end, false) == true end
  end
end

local texts = {
  [1] = function() return 'Dein Klon fährt dein Auto (P' .. state.position .. ').', 'Chat: /play holt ihn an die Box, dann übernimmst du wieder.' end,
  [2] = function()
    local left = math.max(0, state.eta - math.floor(clock - state.at))
    return 'Dein Klon kommt an die Box (P' .. state.position .. ').', 'Wechsel in etwa ' .. math.floor(left / 60) .. ':' .. string.format('%02d', left % 60) .. ' min – du kannst ihm solange zuschauen. /bot = weiterfahren lassen.'
  end,
  [3] = function() return 'Fahrerwechsel!', 'Du wirst jetzt umgesetzt – einen Moment.' end,
  [4] = function() return 'Fahr in deine Box und halte dort an.', 'Dort übernimmt dein Klon, du kannst ihm danach zuschauen. /play = abbrechen.' end,
  [5] = function() return 'Dein Klon fährt gerade dein Auto.', 'Du wirst gleich in ein Ersatzauto in der Box gesetzt und kannst ihm zuschauen.' end,
  [6] = function()
    local left = math.max(0, state.eta - math.floor(clock - state.at))
    return 'Streckenwechsel: ' .. (state.info or ''), 'Der Server startet neu – du wirst in ' .. left .. ' s automatisch wieder verbunden.'
  end
}

function script.drawUI()
  local t = texts[state.phase]
  if t == nil then return end
  local line1, line2 = t()
  local w = ac.getUI().windowSize.x
  local p1, p2 = vec2(w / 2 - 330, 70), vec2(w / 2 + 330, 128)
  ui.drawRectFilled(p1, p2, rgbm(0.05, 0.07, 0.1, 0.78), 8)
  ui.drawRect(p1, p2, rgbm(0.29, 0.64, 1, 0.9), 8)
  ui.drawText(line1, vec2(p1.x + 16, p1.y + 9), rgbm(1, 1, 1, 1))
  ui.drawText(line2, vec2(p1.x + 16, p1.y + 32), rgbm(0.8, 0.85, 0.9, 1))
end
