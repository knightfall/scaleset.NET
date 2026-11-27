"""Exercise the actual handoff shell scripts against a local Linux Docker Engine."""
from contextlib import contextmanager
from pathlib import Path
import re
import subprocess
import sys
import time
import unittest


ROOT = Path(__file__).resolve().parents[2]
PROVIDER = ROOT / "src/ScaleSet.Providers.Docker/DockerRunnerProvider.cs"
SOURCE = PROVIDER.read_text(encoding="utf-8")
TEST_SOURCE = Path(__file__).with_name("DockerRunnerProviderIntegrationTests.cs").read_text(encoding="utf-8")
IMAGE_MATCH = re.search(r'private const string Image = "([^"]+@sha256:[a-f0-9]{64})";', TEST_SOURCE)
if IMAGE_MATCH is None:
    raise RuntimeError("Cannot find the digest-pinned Docker integration fixture image")
IMAGE = IMAGE_MATCH[1]
SENTINEL = "synthetic-jit-verification-only"
HANDOFF = "/run/scaleset-jit/handoff"
RUNNER = 'test "$ACTIONS_RUNNER_INPUT_JITCONFIG" = "$1" || exit 99; printf "%s\n" runner-invoked'


def shell_constant(name):
    body = re.search(rf'private const string {name} = [^\n]*"""\n(.*?)\n        """;', SOURCE, re.S)[1]
    return "\n".join(line.removeprefix("        ") for line in body.splitlines())


CLOCK = shell_constant("HandoffClock")
BOOTSTRAP = CLOCK + "\n" + shell_constant("BootstrapScript")
SENDER = CLOCK + "\n" + shell_constant("JitHandoffScript")


def docker(*commands, timeout=12):
    return subprocess.run(["docker", *commands], check=True, capture_output=True, text=True, timeout=timeout).stdout.strip()


@contextmanager
def container(budget, bootstrap=BOOTSTRAP, runner=RUNNER):
    identity = docker(
        "run", "-d", "--pull=never", "--network=none", "--user=65534:65534",
        "--tmpfs", "/run/scaleset-jit:rw,nosuid,nodev,noexec,size=2m,mode=1777",
        "--entrypoint", "/bin/sh", IMAGE, "-c", bootstrap, "boot", str(budget),
        "/bin/sh", "-c", runner, "runner", SENTINEL,
    )
    try:
        yield identity
    finally:
        docker("rm", "-f", identity)


def sender(identity, budget, script=SENDER):
    return subprocess.Popen([
        "docker", "exec", "-e", f"SCALESET_JIT_CONFIG={SENTINEL}", identity,
        "/bin/sh", "-c", script, "sender", str(budget),
    ], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


def wait_for_file(identity, path):
    docker("exec", identity, "/bin/sh", "-c",
           'for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do '
           'test -f "$1" && exit 0; sleep 0.1; done; exit 1', "wait", path)


def fixture_clock(script):
    start = "monotonic_now() {\n"
    if script.count(start) != 1:
        raise AssertionError("Cannot locate the handoff clock")
    return script.replace(start, start + """    now=0
    if [ -f /tmp/fixture-deadline-elapsed ]; then now=1000; fi
    return 0
""", 1)


def pause_before_final_poll():
    loop = 'while [ ! -f "$handoff/accepted" ] || kill -0 "$sender" 2>/dev/null; do'
    if BOOTSTRAP.count(loop) != 1:
        raise AssertionError("Cannot locate the final handoff poll")
    return fixture_clock(BOOTSTRAP.replace(loop, """printf '%s\\n' "$sender" > /tmp/fixture-sender-pid
: > /tmp/fixture-before-final-poll
until [ -f /tmp/fixture-resume-final-poll ]; do sleep 0.1; done
""" + loop, 1))


def resume_at_deadline(identity):
    docker("exec", identity, "/bin/sh", "-c",
           ": > /tmp/fixture-deadline-elapsed && : > /tmp/fixture-resume-final-poll")


class DockerHandoffTests(unittest.TestCase):
    def test_delayed_directory_claim_and_sender_exit_share_a_deadline(self):
        bootstrap = BOOTSTRAP.replace('mkdir "$handoff"', 'sleep 0.8\nmkdir "$handoff"', 1)
        bootstrap = bootstrap.replace('while [ ! -f "$offer/config" ]; do',
                                      'sleep 0.8\nwhile [ ! -f "$offer/config" ]; do', 1)
        delayed = SENDER.replace('while [ ! -f "$handoff/deadline" ]; do',
                                 'test -f "$handoff/deadline" || echo directory-wait\n'
                                 'while [ ! -f "$handoff/deadline" ]; do', 1)
        delayed = delayed.replace('while [ ! -f "$handoff/claimed" ]; do',
                                  'test -f "$handoff/claimed" || echo claim-wait\n'
                                  'while [ ! -f "$handoff/claimed" ]; do', 1)
        delayed += '\n: > /tmp/fixture-accepted\nsleep 0.8'
        with container(600, bootstrap) as identity:
            with sender(identity, 600, delayed) as handoff:
                wait_for_file(identity, "/tmp/fixture-accepted")
                self.assertEqual("", docker("logs", identity))
                output, errors = handoff.communicate(timeout=8)
                self.assertEqual(0, handoff.returncode, errors)
                self.assertEqual(["directory-wait", "claim-wait"], output.splitlines())
            self.assertEqual("0", docker("wait", identity))
            self.assertEqual("runner-invoked", docker("logs", identity))

    def test_deadline_aborts_without_a_handoff(self):
        started = time.monotonic()
        with container(120) as identity:
            self.assertEqual("125", docker("wait", identity, timeout=5))
            self.assertEqual("", docker("logs", identity))
        self.assertLess(time.monotonic() - started, 5)

    def test_accepted_sender_exit_survives_a_late_final_poll(self):
        runner = 'test -z "$(find /run/scaleset-jit -mindepth 1)" || exit 98\n' + RUNNER
        with container(100, pause_before_final_poll(), runner) as identity:
            with sender(identity, 100, fixture_clock(SENDER)) as handoff:
                wait_for_file(identity, "/tmp/fixture-before-final-poll")
                _, errors = handoff.communicate(timeout=5)
                self.assertEqual(0, handoff.returncode, errors)
            wait_for_file(identity, HANDOFF + "/accepted")
            docker("exec", identity, "/bin/sh", "-c",
                   'read -r pid < /tmp/fixture-sender-pid; ! kill -0 "$pid" 2>/dev/null')
            self.assertEqual("", docker("logs", identity))
            resume_at_deadline(identity)
            self.assertEqual("0", docker("wait", identity, timeout=5))
            self.assertEqual("runner-invoked", docker("logs", identity))

    def test_deadline_with_acceptance_but_sender_still_alive_prevents_dispatch(self):
        blocked_sender = fixture_clock(SENDER) + "\n: > /tmp/fixture-sender-held\nwhile :; do sleep 1; done"
        with container(100, pause_before_final_poll()) as identity:
            with sender(identity, 100, blocked_sender) as handoff:
                wait_for_file(identity, "/tmp/fixture-before-final-poll")
                wait_for_file(identity, "/tmp/fixture-sender-held")
                wait_for_file(identity, HANDOFF + "/accepted")
                self.assertIsNone(handoff.poll())
                docker("exec", identity, "/bin/sh", "-c",
                       'read -r pid < /tmp/fixture-sender-pid; kill -0 "$pid"')
                resume_at_deadline(identity)
                self.assertEqual("125", docker("wait", identity, timeout=5))
                handoff.communicate(timeout=5)
                self.assertNotEqual(0, handoff.returncode)
            self.assertEqual("", docker("logs", identity))

    def test_deadline_after_claim_without_acceptance_prevents_dispatch(self):
        acceptance = ': > "$handoff/accepted"'
        self.assertEqual(1, SENDER.count(acceptance))
        unaccepted = fixture_clock(SENDER.replace(acceptance, "exit 17", 1))
        with container(100, pause_before_final_poll()) as identity:
            with sender(identity, 100, unaccepted) as handoff:
                wait_for_file(identity, "/tmp/fixture-before-final-poll")
                handoff.communicate(timeout=5)
                self.assertEqual(17, handoff.returncode)
            docker("exec", identity, "test", "!", "-e", HANDOFF + "/accepted")
            resume_at_deadline(identity)
            self.assertEqual("125", docker("wait", identity, timeout=5))
            self.assertEqual("", docker("logs", identity))

    def test_sender_cancellation_does_not_dispatch_the_runner(self):
        interrupted = SENDER.replace('while [ ! -f "$handoff/claimed" ]; do',
                                      'printf "%s\n" "$$" > /tmp/fixture-sender\n'
                                      'sleep 2\nwhile [ ! -f "$handoff/claimed" ]; do', 1)
        with container(430) as identity:
            with sender(identity, 430, interrupted) as handoff:
                wait_for_file(identity, "/tmp/fixture-sender")
                docker("exec", identity, "/bin/sh", "-c",
                       'read -r pid < /tmp/fixture-sender; kill -TERM "$pid"')
                handoff.communicate(timeout=5)
                self.assertNotEqual(0, handoff.returncode)
            self.assertEqual("125", docker("wait", identity, timeout=7))
            self.assertEqual("", docker("logs", identity))


    def test_failed_staging_clears_the_temporary_payload_and_blocks_reuse(self):
        failed_stage = SENDER.replace('mv "$offer/.incoming" "$offer/config"', 'false', 1)
        self.assertNotEqual(failed_stage, SENDER)
        with container(600) as identity:
            with sender(identity, 600, failed_stage) as handoff:
                handoff.communicate(timeout=8)
                self.assertEqual(125, handoff.returncode)

            docker("exec", identity, "/bin/sh", "-c",
                   'test -d "$1/offer" && test -z "$(find "$1/offer" -type f)"', "inspect", HANDOFF)
            with sender(identity, 600) as handoff:
                handoff.communicate(timeout=8)
                self.assertEqual(125, handoff.returncode)
            self.assertEqual("", docker("logs", identity))

    def test_sender_timeout_clears_an_unclaimed_payload_while_bootstrap_is_alive(self):
        delayed = BOOTSTRAP.replace('while [ ! -f "$offer/config" ]; do',
                                    'sleep 4\nwhile [ ! -f "$offer/config" ]; do', 1)
        with container(200, delayed) as identity:
            with sender(identity, 200) as handoff:
                wait_for_file(identity, HANDOFF + "/offer/config")
                docker("exec", identity, "grep", "-qF", SENTINEL, HANDOFF + "/offer/config")
                handoff.communicate(timeout=5)
                self.assertEqual(125, handoff.returncode)

            docker("exec", identity, "/bin/sh", "-c",
                   'test -z "$(find "$1/offer" -type f)"', "inspect", HANDOFF)
            self.assertEqual("125", docker("wait", identity, timeout=6))

    def test_success_clears_staging_files_before_the_runner_uses_the_mount(self):
        runner = RUNNER + '\n: > /tmp/fixture-runner\nsleep 5'
        with container(600, runner=runner) as identity:
            with sender(identity, 600) as handoff:
                _, errors = handoff.communicate(timeout=7)
                self.assertEqual(0, handoff.returncode, errors)
            wait_for_file(identity, "/tmp/fixture-runner")
            self.assertEqual("runner-invoked", docker("logs", identity))
            docker("exec", identity, "/bin/sh", "-c", 'test -z "$(find /run/scaleset-jit -mindepth 1)"')

    def test_force_stop_discards_payload_bytes_with_the_tmpfs(self):
        delayed = BOOTSTRAP.replace('while [ ! -f "$offer/config" ]; do',
                                    'sleep 5\nwhile [ ! -f "$offer/config" ]; do', 1)
        with container(700, delayed) as identity:
            with sender(identity, 700) as handoff:
                wait_for_file(identity, HANDOFF + "/offer/config")
                docker("exec", identity, "grep", "-qF", SENTINEL, HANDOFF + "/offer/config")
                docker("kill", identity)
                handoff.communicate(timeout=5)
                self.assertNotEqual(0, handoff.returncode)

            docker("start", identity)
            wait_for_file(identity, HANDOFF + "/deadline")
            docker("exec", identity, "/bin/sh", "-c", 'test ! -e "$1/offer"', "inspect", HANDOFF)
            self.assertEqual("", docker("logs", identity))


if __name__ == "__main__":
    if sys.argv[1:] == ["--fixture-image"]:
        print(IMAGE)
    else:
        unittest.main(verbosity=2)
