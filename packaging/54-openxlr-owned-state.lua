-- WirePlumber 0.4 compares state.restore-props with a Lua boolean, but node
-- properties arrive as strings. Honor the opt-out declared by OpenXLR's
-- software nodes so desktop state cannot replace the mixer's saved intent.
table.insert(stream_defaults.rules, {
  matches = {
    {
      { "node.name", "matches", "OpenXLR_*" },
      { "state.restore-props", "equals", "false" },
    },
  },
  apply_properties = {
    ["state.restore-props"] = false,
  },
})
