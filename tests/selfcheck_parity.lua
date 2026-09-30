-- Runs the parity scenario against the Lua module (tests/lua/otc_selfcheck.lua, from ue4ss-mod-test-kit) and prints its
-- [SELFCHECK] lines, one per line. OtcLib.Tests runs the same scenario through OtcSelfCheck and compares line by line.
-- Usage: lua selfcheck_parity.lua <path to otc_selfcheck.lua>
local path = arg[1] or "lua/otc_selfcheck.lua"
local M = dofile(path)

print = function(s)                      -- the module prints line .. "\n"
    s = tostring(s):gsub("\n+$", "")
    io.write(s, "\n")
end

local SC = M.new("Parity Mod")
SC:expect("a", "load a save")
SC:expect("b", "press Num 4", { optional = true })
SC:expect("c", "hold it", { grace = 3 })
SC:expect("a", "dup ignored")
SC:wait("a", "not in a run")
SC:wait("a", "not in a run")
SC:wait("a", "menu")
SC:wait("a", "paused")
SC:wait("a", "fourth reason")
SC:check("c", false, "miss one")
SC:check("c", false, "miss two")
SC:check("c", false, "miss three")
SC:pass("c", "later ok")
SC:pass("a", "value 5")
SC:pass("a", "again")
SC:fail("a", "broke")
SC:pass("a", "x")
SC:expect("d", "multi\nline")
SC:fail_open("gave up\r\nnow")
SC:expect("long id with spaces", string.rep("x", 400))
SC:check("unknown", true, "nope")
SC:wait("b", "after result")
SC:expect("e", "grace one")
SC:check("e", false, "missed")
SC:expect("f", "direct")
SC:check("f", true, "ok at once")
SC:check("f", false, "ignored after result")
