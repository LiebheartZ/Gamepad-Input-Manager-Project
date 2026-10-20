"""GDM entry point with a diagnostic console."""

import sys

from gdm_manager import run_application


if __name__ == "__main__":
    run_application(start_in_tray="--start-in-tray" in sys.argv[1:])
