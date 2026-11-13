"""Focused stdio MCP acceptance. --rendered opts into the existing Sandbox."""
import argparse
import json
import queue
import subprocess
import threading
import time
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', required=True)
    parser.add_argument('--build-root', required=True)
    parser.add_argument('--rendered', action='store_true')
    parser.add_argument('--replay-only', action='store_true')
    parser.add_argument('--ownership-only', action='store_true')
    parser.add_argument('--control-only', action='store_true')
    parser.add_argument('--output', default='artifacts/ai-harness-review/result.json')
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    errors = open(output.with_suffix('.log'), 'w', encoding='utf-8')
    process = subprocess.Popen(['dotnet', str(Path(args.host).resolve()), 'mcp', '--build-root', str(Path(args.build_root).resolve())],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors, text=True, encoding='utf-8')
    messages = queue.Queue()

    def receive():
        for line in process.stdout:
            try:
                messages.put(json.loads(line))
            except Exception as error:
                messages.put({'invalid_stdout': str(error), 'line': line[:300]})
        messages.put({'closed': True})

    threading.Thread(target=receive, daemon=True).start()
    counter = 0
    metrics = {}
    session = None

    def rpc(method, params, timeout=260):
        nonlocal counter
        counter += 1
        process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': counter, 'method': method, 'params': params}) + '\n')
        process.stdin.flush()
        end = time.monotonic() + timeout
        while True:
            message = messages.get(timeout=max(.1, end - time.monotonic()))
            if 'closed' in message or 'invalid_stdout' in message:
                raise RuntimeError(message)
            if message.get('id') == counter:
                if 'error' in message:
                    raise RuntimeError(message['error'])
                return message['result']

    def tool(name, arguments=None):
        response = rpc('tools/call', {'name': name, 'arguments': arguments or {}})
        if response.get('isError'):
            raise RuntimeError(response.get('content'))
        return response

    def value(name, arguments=None):
        result = tool(name, arguments)
        return result.get('structuredContent') or json.loads(next(c['text'] for c in result['content'] if c['type'] == 'text'))

    def observe(sections=None):
        response = value('game_observe', {'session': session, 'sections': sections or []})
        if response['outcome'] != 'passed':
            raise RuntimeError(response)
        return response['data']['observation']

    def action(kind, data, wait=True, request_id=None, client=1, allow_failure=False):
        response = value('game_action', {'session': session, 'request': {'id': request_id or str(uuid.uuid4()), 'action': kind, 'data': data, 'client': client, 'timeoutSeconds': 60}})
        if response['outcome'] != 'accepted':
            raise RuntimeError(response)
        operation = response['request']
        if not wait:
            return operation
        while True:
            result = value('game_operation', {'session': session, 'id': operation, 'waitSeconds': 10})['data']
            if result['state'] in ('queued', 'running'):
                continue
            if result['state'] != 'completed' and not allow_failure:
                raise RuntimeError(result)
            return result['result']

    try:
        rpc('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'Stargate acceptance', 'version': '1'}})
        process.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
        process.stdin.flush()
        names = {t['name'] for t in rpc('tools/list', {})['tools']}
        assert names == {'game_catalog', 'game_prepare', 'game_sessions', 'game_start', 'game_attach', 'game_observe', 'game_capture', 'game_action', 'game_operation', 'game_recipe', 'game_detach', 'game_stop'}, names
        catalog = value('game_catalog')
        assert len(catalog['actions']) >= 37
        metrics['tools'] = sorted(names)
        print('MCP discovery passed', flush=True)
        if args.rendered:
            started = time.monotonic()
            manifest = value('game_start', {'options': {'scenario': 'sandbox', 'cold': True}})
            session = manifest['id']
            metrics['session'] = session
            metrics['coldLaunchSeconds'] = time.monotonic() - started
            print('Sandbox ready:', session, flush=True)
            if args.control_only:
                issues = []
                for name in ['game_observe', 'game_capture']:
                    invalid = rpc('tools/call', {'name': name, 'arguments': {'session': 'invalid'}})
                    if not invalid.get('isError') or not invalid.get('structuredContent'):
                        issues.append(name + ' errors lack structuredContent')
                action('add-client', {})
                recipe_path = Path(manifest['root']) / 'control-regression.json'
                def start_recipe(steps):
                    recipe_path.write_text(json.dumps({'name': 'control-regression', 'steps': steps}), encoding='utf-8')
                    return value('game_recipe', {'session': session, 'name': str(recipe_path)})['request']
                def finish(operation):
                    while True:
                        result = value('game_operation', {'session': session, 'id': operation, 'waitSeconds': 10})['data']
                        if result['state'] not in ('queued', 'running'):
                            return result
                def input_active():
                    return value('game_observe', {'session': session, 'targetClient': 2})['data']['observation']['control']['syntheticInputActive']
                held_step = {'action': 'input', 'client': 2, 'data': {'seconds': 20, 'keys': ['Q'], 'hold': True}}
                operation = start_recipe([held_step, {'action': 'wait', 'client': 2, 'timeoutSeconds': 1, 'data': {'path': 'neverExists', 'present': True}}])
                timed_out = finish(operation)
                if timed_out['state'] != 'timed-out':
                    issues.append('Recipe timeout reported as ' + timed_out['state'])
                if input_active():
                    issues.append('Failed recipe left client 2 input held')
                action('input', {'seconds': 0}, client=2)
                operation = start_recipe([{'action': 'input', 'client': 2, 'data': {'seconds': 20, 'keys': ['Q']}}])
                deadline = time.monotonic() + 10
                while not input_active():
                    assert time.monotonic() < deadline, 'Recipe input did not start'
                value('game_operation', {'session': session, 'id': operation, 'cancel': True})
                cancelled = finish(operation)
                assert cancelled['state'] == 'cancelled', cancelled
                if input_active():
                    issues.append('Cancelled recipe left client 2 input held')
                # Cancel the client operation by ending the session if it is still pending.
                if input_active():
                    value('game_detach', {'session': session})
                else:
                    completed = finish(start_recipe([held_step]))
                    assert completed['state'] == 'completed', completed
                    if input_active():
                        issues.append('Completed recipe left client 2 input held')
                    client_timeout = finish(start_recipe([{'action': 'input', 'client': 2, 'timeoutSeconds': 1, 'data': {'seconds': 20, 'keys': ['Q']}}]))
                    if client_timeout['state'] != 'timed-out' or input_active():
                        issues.append('Client-side step timeout did not finalize cleanly: ' + client_timeout['state'])
                metrics['issues'] = issues
                assert not issues, issues
                metrics['outcome'] = 'passed'
                print('Recipe timeout, cancellation, completion cleanup and structured errors passed', flush=True)
                return
            if args.ownership_only:
                held = action('input', {'seconds': 25, 'keys': ['Q'], 'hold': True})
                assert observe()['control']['syntheticInputActive']
                value('game_operation', {'session': session, 'id': held['request'], 'cancel': True})
                assert not observe()['control']['syntheticInputActive']
                # Identical retries preserve one authoritative spawn.
                request_id = str(uuid.uuid4())
                command = {'command': {'action': 'spawn', 'point': {'x': -12, 'y': .05, 'z': 12}}}
                action('npc', command, request_id=request_id)
                action('npc', command, request_id=request_id)
                assert len(observe(['npcs'])['npcs']['records']) == 1
                conflict = rpc('tools/call', {'name': 'game_action', 'arguments': {'session': session, 'request': {'id': request_id, 'action': 'npc', 'data': command, 'client': 2, 'timeoutSeconds': 60}}})
                assert conflict.get('isError'), conflict
                action('add-client', {})
                request_file = output.with_suffix('.request.json')
                request_file.write_text(json.dumps({'action': 'observe', 'client': 2}), encoding='utf-8')
                cli = subprocess.run(['dotnet', str(Path(args.host).resolve()), 'action', '--session', session, '--request', str(request_file)], capture_output=True, text=True, check=True)
                assert json.loads(cli.stdout)['client'] == 2, cli.stdout
                second = value('game_observe', {'session': session, 'targetClient': 2, 'sections': ['npcs']})
                deadline = time.monotonic() + 5
                while not second['data']['observation']['npcs']['records']:
                    assert time.monotonic() < deadline
                    second = value('game_observe', {'session': session, 'targetClient': 2, 'sections': ['npcs']})
                request_file.unlink()
                action('reset-scenario', {})
                assert not observe(['npcs'])['npcs']['records']
                assert not value('game_observe', {'session': session, 'targetClient': 2, 'sections': ['npcs']})['data']['observation']['npcs']['records']
                stale = value('game_action', {'session': session, 'request': {'action': 'move', 'generation': 1, 'data': {'seconds': .1}}})
                stale_result = value('game_operation', {'session': session, 'id': stale['request'], 'waitSeconds': 10})['data']
                assert stale_result['state'] == 'failed' and 'generation changed' in stale_result['result']['error'].lower(), stale_result
                # Abrupt host loss must close its session via coordinator ownership.
                process.kill(); process.wait(timeout=10)
                deadline = time.monotonic() + 25
                record = Path(manifest['root']) / 'session.json'
                while json.loads(record.read_text(encoding='utf-8'))['state'] not in ('stopped', 'failed'):
                    assert time.monotonic() < deadline, 'Owner exit did not stop coordinator'
                    time.sleep(.1)
                metrics['ownership'] = 'hold cancellation, deduplication, client-file selection, two-client replication, abrupt host exit'
                metrics['outcome'] = 'passed'
                print('Ownership and retry checks passed', flush=True)
                session = None
                return
            if args.replay_only:
                action('teleport', {'station': 'props'})
                action('npc', {'command': {'action': 'spawn', 'point': {'x': -12, 'y': .05, 'z': 12}}})
                action('wait', {'path': 'npcs.records.0', 'present': True})
                npc = observe(['npcs'])['npcs']['records'][0]
                action('aim', {'entity': npc['id']})
                deadline = time.monotonic() + 5
                while True:
                    picked = value('game_observe', {'session': session, 'pickX': .5, 'pickY': .5})['data']['observation']['pick']
                    if picked['npc'] == npc['id']:
                        break
                    assert time.monotonic() < deadline, picked
                failure = action('assert', {'path': 'npcs.records.0.position.y', 'equals': 100000}, allow_failure=True)
                assert failure['outcome'] == 'failed'
                replay = value('game_recipe', {'session': session, 'export': True})
                metrics['replay'] = replay
                operation = value('game_recipe', {'session': session, 'name': replay['path']})['request']
                while True:
                    replayed = value('game_operation', {'session': session, 'id': operation, 'waitSeconds': 10})['data']
                    if replayed['state'] not in ('queued', 'running'):
                        break
                assert replayed['state'] == 'failed', replayed
                assert 'Assertion failed' in replayed['result']['error'], replayed
                assert observe()['generation'] == 2
                metrics['outcome'] = 'passed'
                print('NPC failure inspection and replay passed', flush=True)
                return

            started = time.monotonic()
            state = observe(['ui'])
            metrics['observeMilliseconds'] = (time.monotonic() - started) * 1000
            metrics['observationBytes'] = len(json.dumps(state).encode())
            movement = action('move', {'seconds': 10, 'forward': .2}, wait=False)
            deadline = time.monotonic() + 5
            while not observe()['control']['syntheticInputActive']:
                if time.monotonic() > deadline:
                    raise TimeoutError('Movement did not start')
            value('game_operation', {'session': session, 'id': movement, 'cancel': True})
            while value('game_operation', {'session': session, 'id': movement, 'waitSeconds': 10})['data']['state'] in ('queued', 'running'):
                pass
            assert not observe()['control']['syntheticInputActive']
            action('teleport', {'station': 'props'})
            action('aim', {'target': {'x': -11, 'y': .1, 'z': 5}})
            action('input', {'seconds': 20, 'keys': ['Q'], 'hold': True})
            # Observe a subsequent rendered UI frame rather than using startup sleeps.
            deadline = time.monotonic() + 10
            while True:
                ui = observe(['ui'])['ui']
                lantern = next((c for c in ui['controls'] if c['id'] == 'spawn/wooden-lantern/Prop'), None)
                if lantern:
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError('Spawn menu not visible')
            action('input', {'seconds': .2, 'keys': ['Q'], 'controlId': lantern['id'], 'uiRevision': ui['revision']})
            # Selecting a tile does not spawn. Use a separate click in the world.
            assert not any(p['kind'] == 'wooden-lantern' for p in observe()['nearby']['items'])
            action('input', {'seconds': .1, 'keys': ['Q'], 'x': .6, 'y': .55})
            action('input', {'seconds': .2, 'keys': ['Q'], 'buttons': ['Left'], 'x': .6, 'y': .55})
            deadline = time.monotonic() + 10
            while not any(p['kind'] == 'wooden-lantern' for p in observe()['nearby']['items']):
                if time.monotonic() > deadline:
                    raise TimeoutError('Lantern spawn did not replicate')
            lamp = next(p for p in observe()['nearby']['items'] if p['kind'] == 'wooden-lantern')
            action('aim', {'entity': lamp['id']})
            action('input', {'seconds': .2, 'keys': ['G']})
            action('input', {'seconds': 10, 'buttons': ['Left'], 'hold': True})
            deadline = time.monotonic() + 5
            while not observe(['physics'])['physics']['carries']:
                if time.monotonic() > deadline:
                    raise TimeoutError('Physics gun did not grab the aimed lantern')
            action('input', {'seconds': .3, 'buttons': ['Left'], 'dx': 90, 'dy': -30, 'scroll': 1})
            action('input', {'seconds': .2, 'keys': ['G']})
            action('input', {'seconds': .2, 'keys': ['I']})
            inventory_ui = observe(['ui'])['ui']
            assert inventory_ui['inventoryOpen']
            action('input', {'seconds': .2, 'controlId': 'inventory/action/holster', 'uiRevision': inventory_ui['revision']})
            deadline = time.monotonic() + 5
            while observe(['inventory'])['inventory']['personal']['equipped'] is not None:
                assert time.monotonic() < deadline, 'Inventory holster did not replicate'
            inventory_ui = observe(['ui'])['ui']
            action('input', {'seconds': .2, 'controlId': 'inventory/personal/0', 'uiRevision': inventory_ui['revision']})
            inventory_ui = observe(['ui'])['ui']
            action('input', {'seconds': .2, 'controlId': 'inventory/action/equip', 'uiRevision': inventory_ui['revision']})
            deadline = time.monotonic() + 5
            while observe(['inventory'])['inventory']['personal']['equipped'] is None:
                assert time.monotonic() < deadline, 'Inventory equip did not replicate'
            action('input', {'seconds': .2, 'keys': ['I']})
            action('input', {'seconds': 10, 'keys': ['B'], 'hold': True})
            deadline = time.monotonic() + 5
            while True:
                building_ui = observe(['ui'])['ui']
                exit_build = next((c for c in building_ui['controls'] if c['id'] == 'building/exit'), None)
                if exit_build:
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError('Building picker did not open')
            action('input', {'seconds': .2, 'keys': ['B'], 'controlId': exit_build['id'], 'uiRevision': building_ui['revision']})
            print('Before capture:', json.dumps(observe(['diagnostics'])['diagnostics']), flush=True)
            capture = tool('game_capture', {'session': session, 'retain': True, 'name': 'mcp-sandbox.png'})
            assert any(c['type'] == 'image' for c in capture['content'])
            metrics['capture'] = capture['structuredContent']['response']['data']['path']
            started = time.monotonic()
            action('reset-scenario', {})
            metrics['warmResetSeconds'] = time.monotonic() - started
            action('add-client', {})
            second = value('game_observe', {'session': session, 'targetClient': 2})
            assert second['data']['observation']['readiness']['playable']
            value('game_stop', {'session': session})
            value('game_stop', {'session': session})
            metrics['retainedBytes'] = sum(p.stat().st_size for p in Path(manifest['root']).rglob('*') if p.is_file())
            assert not list(Path(manifest['root']).glob('world-*/*'))
            print('Rendered MCP checks passed', flush=True)
        metrics['outcome'] = 'passed'
    except Exception as error:
        metrics['outcome'] = 'failed'
        metrics['error'] = str(error)
        raise
    finally:
        if session:
            try:
                value('game_stop', {'session': session})
            except Exception:
                pass
        try:
            process.stdin.close()
        except OSError:
            pass
        try:
            process.wait(timeout=25)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        errors.close()
        output.write_text(json.dumps(metrics, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
