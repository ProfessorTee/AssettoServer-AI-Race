-- Server tools: banner when the server changes the track (sent by the server to CSP clients).
-- The server restarts with the next track; the game has to load it, so the player rejoins via Content Manager after the countdown.

local info, eta, at, active = '', 0, 0, false
local clock = 0

ac.OnlineEvent({
  ac.StructItem.key('ST_trackchange'),
  eta = ac.StructItem.uint16(),
  info = ac.StructItem.string(48)
}, function(sender, data)
  if sender ~= nil then return end
  info = data.info or ''
  eta = data.eta
  at = clock
  active = true
end)

function script.update(dt)
  clock = clock + dt
end

function script.drawUI()
  if not active then return end
  local left = math.max(0, eta - math.floor(clock - at))
  local line2 = left > 0
    and ('Der Server startet neu. In etwa ' .. left .. ' s über Content Manager neu beitreten.')
    or 'Jetzt über Content Manager neu beitreten (Server in der Liste auswählen).'
  local w = ac.getUI().windowSize.x
  local p1, p2 = vec2(w / 2 - 330, 70), vec2(w / 2 + 330, 128)
  ui.drawRectFilled(p1, p2, rgbm(0.05, 0.07, 0.1, 0.78), 8)
  ui.drawRect(p1, p2, rgbm(0.29, 0.64, 1, 0.9), 8)
  ui.drawText('Streckenwechsel: ' .. info, vec2(p1.x + 16, p1.y + 9), rgbm(1, 1, 1, 1))
  ui.drawText(line2, vec2(p1.x + 16, p1.y + 32), rgbm(0.8, 0.85, 0.9, 1))
end
