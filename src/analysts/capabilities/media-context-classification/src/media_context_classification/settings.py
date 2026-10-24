DETECTOR_VERSION = "1.0.0"
FRAME_WIDTH = 64
FRAME_HEIGHT = 36
HARD_THRESHOLD = 30.0
ADAPTIVE_FLOOR = 8.0
ADAPTIVE_RATIO = 3.0
ADAPTIVE_WINDOW = 4
MIN_SHOT_LENGTH_MS = 500
RULE_ORDER = ("hard-cut", "adaptive-cut")
CONFIDENCE = 0.5

settings = {
    "detectorVersion": DETECTOR_VERSION,
    "frameWidth": FRAME_WIDTH,
    "frameHeight": FRAME_HEIGHT,
    "adaptiveWindow": ADAPTIVE_WINDOW,
    "minShotLengthMs": MIN_SHOT_LENGTH_MS,
    "ruleOrder": list(RULE_ORDER),
}
