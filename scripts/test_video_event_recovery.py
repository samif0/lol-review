"""Regression checks for model-output rejection; no inference/runtime required."""
import importlib.util
import json
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("probe", Path(__file__).with_name("Probe-VideoEventRecovery.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class ValidationTests(unittest.TestCase):
    def validate(self, card):
        return probe.validate_card(json.dumps(card), "Miss Fortune", ["Miss Fortune", "Leona"], ["Caitlyn"], 8)

    def card(self, target="Caitlyn"):
        return {"local_champion":"Miss Fortune", "classification":"reciprocal_exchange", "directions":[
            {"attacker":"Miss Fortune", "target":target,"frames":[2]},
            {"attacker":target, "target":"Miss Fortune","frames":[3]}]}

    def test_observed_friendly_fire_hallucination_rejected(self):
        result = self.validate(self.card("Leona"))
        self.assertFalse(result["structurally_valid"])
        self.assertIn("Damage direction contradicts roster", result["rejection_reasons"])

    def test_timestamp_masquerading_as_frame_reference_rejected(self):
        card = self.card()
        card["directions"][0]["frames"] = [24.38]
        self.assertFalse(self.validate(card)["structurally_valid"])

    def test_two_outgoing_attacks_do_not_establish_reciprocity(self):
        card = self.card()
        card["directions"][1] = card["directions"][0]
        self.assertFalse(self.validate(card)["structurally_valid"])

    def test_structurally_plausible_card_is_still_not_verified(self):
        result = self.validate(self.card())
        self.assertTrue(result["structurally_valid"])
        self.assertFalse(result["eligible_for_publication"])
        self.assertTrue(result["requires_visual_validation"])

    def test_invalid_json_does_not_publish(self):
        result = probe.validate_card("I think there was a fight", "Miss Fortune", [], [], 8)
        self.assertFalse(result["eligible_for_publication"])
        self.assertFalse(result["structurally_valid"])


if __name__ == "__main__":
    unittest.main()
