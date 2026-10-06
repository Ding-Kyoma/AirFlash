"""Mute restoration fault injection without opening a real audio endpoint."""

import pytest

from scripts import windows_audio


class Endpoint:
    def __init__(self, muted=False, accepts=True, restore_fails=False):
        self.value = muted
        self.accepts = accepts
        self.restore_fails = restore_fails
        self.closed = False
        self.writes = []

    @property
    def muted(self):
        return self.value if self.accepts else False

    @muted.setter
    def muted(self, value):
        self.writes.append(value)
        if len(self.writes) > 1 and self.restore_fails:
            raise OSError("endpoint disconnected")
        self.value = value

    def close(self):
        self.closed = True


@pytest.mark.parametrize("previous", [False, True])
def test_guard_restores_original_endpoint_after_probe_exception(monkeypatch, previous):
    endpoint = Endpoint(previous)
    monkeypatch.setattr(windows_audio, "_DefaultEndpoint", lambda: endpoint)
    with pytest.raises(RuntimeError), windows_audio.MutedOutput():
        assert endpoint.muted
        # A changed default must not redirect restoration to a different endpoint.
        monkeypatch.setattr(windows_audio, "_DefaultEndpoint", lambda: Endpoint())
        raise RuntimeError("probe failed")
    assert endpoint.writes == [True, previous]
    assert endpoint.value == previous
    assert endpoint.closed


def test_guard_closes_and_restores_after_muting_cannot_be_verified(monkeypatch):
    endpoint = Endpoint(accepts=False)
    monkeypatch.setattr(windows_audio, "_DefaultEndpoint", lambda: endpoint)
    with pytest.raises(OSError), windows_audio.MutedOutput():
        pytest.fail("probe must not play when local mute failed")
    assert endpoint.writes == [True, False]
    assert endpoint.closed


def test_restoration_error_is_reported_and_com_is_released(monkeypatch):
    endpoint = Endpoint(restore_fails=True)
    monkeypatch.setattr(windows_audio, "_DefaultEndpoint", lambda: endpoint)
    with pytest.raises(OSError, match="disconnected"), windows_audio.MutedOutput():
        pass
    assert endpoint.closed
