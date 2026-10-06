# Populated-profile startup fixes

The private imported profile exposed source contracts that synthetic fixtures had not exercised. Three finite zero-scale display rows are valid placeholders in the selected client data. The display/model reader now retains finite zero values for the existing native-scale and collision-height fallback, while negative and nonfinite values remain invalid. Field offsets and duplicate/linkage checks remain unchanged.

Failed host startup also exposed a SocialFeature teardown lookup after dependency-provider disposal. The feature now caches its optional spell collaborator while the provider is alive and unsubscribes through that reference. Write-drain and login/guild task errors still propagate; the fix does not suppress unsaved state.

The direct NPC importer now refuses rows with unsupported behavior-bearing requirements, charges or scripts. Nonzero trainer ability/condition requirements and paid/scripted gossip are skipped with bounded diagnostics. Broadcast-only text is skipped when the current inline model cannot resolve it; valid inline text alongside alternate broadcast IDs is retained. Mapped conditions and default menu zero remain supported. Template inheritance and random spawn selection are separate pending contracts.

Local source qualification: 14,777 passed, six existing skips, zero failures; focus187/native59/clean Release. Frozen export and ZIP checks verified. Populated-profile startup/admission/save remains a separate pending gate. Prior bundles and original live profile preserved.
