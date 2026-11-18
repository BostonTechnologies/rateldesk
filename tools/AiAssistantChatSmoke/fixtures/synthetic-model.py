"""Disposable loopback model; real Netclaw owns actors, sessions and tools.

Run only one continuation smoke at a time. The receiver releases child work
after observing its durable pending state; no timing sleep establishes success.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import json
import re
import threading
import time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--port', type=int, default=19598)
parser.add_argument('--events', type=Path, help='Optional phase/role receipt file; no message content is logged.')
parser.add_argument('--scheduled-reports', action='store_true', help='Serve only the disposable scheduled-report fixture.')
args = parser.parse_args()
pending = threading.Event()
release_child = threading.Event()
scheduled_gates = defaultdict(threading.Event)


def scheduled_response(messages, tools):
    if not tools:
        return 'title', None, 'Disposable scheduled report', None
    user = next((str(message.get('content', '')) for message in reversed(messages) if message.get('role') == 'user'), '')
    final = re.search(r'Return exactly RD-SCHEDULED-FINAL-([a-f0-9]{12})', user)
    if final:
        marker = final[1]
        if not scheduled_gates['report', marker].wait(60):
            raise TimeoutError('Browser did not observe pending work')
        return 'scheduled-final', marker, f'RD-SCHEDULED-FINAL-{marker}', None

    request = re.search(r'RD-SCHEDULE-SMOKE (buffered|post-parent) ([a-f0-9]{12})', user)
    if not request:
        return 'title', None, 'Disposable scheduled report', None

    mode, marker = request.groups()
    if any(call['function']['name'] == 'set_reminder' for message in messages for call in message.get('tool_calls') or []):
        if mode == 'buffered' and not scheduled_gates['parent', marker].wait(60):
            raise TimeoutError('Native dispatch was not observed before the parent completed')
        return 'scheduled-parent-final', marker, f'RD-SCHEDULED-REGISTERED-{marker}', None

    if not any(tool.get('function', {}).get('name') == 'set_reminder' for tool in tools):
        if any(call['function']['name'] == 'load_tool' for message in messages for call in message.get('tool_calls') or []):
            raise ValueError('The disposable profile did not load the native set_reminder tool')
        call = {'id': f'load-schedule-{marker}', 'type': 'function',
                'function': {'name': 'load_tool', 'arguments': json.dumps({
                    'Name': 'set_reminder', '_rationale': 'Load scheduling for the requested disposable reminder.'})}}
        return 'scheduled-load-tool', marker, None, [call]
    arguments = {'Id': f'rd-smoke-{marker}', 'Name': 'Disposable scheduled report',
                 'Prompt': f'Return exactly RD-SCHEDULED-FINAL-{marker}', 'ScheduleType': 'once',
                 'Schedule': '30s', 'DeliveryKind': 'current_session',
                 '_rationale': 'Register the explicitly requested harmless one-shot report.'}
    call = {'id': f'schedule-{marker}', 'type': 'function',
            'function': {'name': 'set_reminder', 'arguments': json.dumps(arguments)}}
    return 'scheduled-register', marker, None, [call]

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def json_response(self, value, status=200):
        body = json.dumps(value).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.endswith('/models'):
            return self.json_response({'object':'list','data':[{'id':'synthetic-native-acceptance','object':'model','owned_by':'synthetic','context_length':32768}]})
        self.json_response({'fixture':'ready'})

    def do_POST(self):
        gate = re.fullmatch(r'/fixture/release-(parent|report)/([a-f0-9]{12})', self.path)
        if args.scheduled_reports and gate:
            scheduled_gates[gate.groups()].set()
            return self.json_response({'released': True})
        if self.path == '/fixture/release-child':
            release_child.set()
            return self.json_response({'released':True})
        body = json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))))
        messages = body.get('messages', [])
        systems = '\n'.join(str(m.get('content','')) for m in messages if m.get('role') == 'system')
        tool_calls = None
        marker = None
        if args.scheduled_reports:
            try:
                phase, marker, content, tool_calls = scheduled_response(messages, body.get('tools'))
            except (TimeoutError, ValueError) as error:
                return self.json_response({'error': str(error)}, 500)
        elif not body.get('tools'):
            phase = 'title'
            content = 'Disposable arithmetic acceptance'
        elif 'SYNTHETIC_CHILD_EXECUTOR' in systems:
            phase = 'child-result'
            if not pending.wait(30):
                return self.json_response({'error':'Parent did not reach pending reply'}, 500)
            # The receiver explicitly proves its durable pending state before releasing work.
            if not release_child.wait(90):
                return self.json_response({'error':'Receiver did not release the disposable child'}, 500)
            content = 'Synthetic child completed: 2 + 2 = 4.'
        elif any('spawn_agent' in str(m.get('tool_calls', [])) for m in messages):
            notices = [m for m in messages if m.get('role') == 'user' and 'result.md' in str(m.get('content',''))]
            if notices:
                phase = 'parent-final'
                content = 'Synthetic delegated report complete: 2 + 2 = 4.'
            else:
                phase = 'parent-pending'
                content = 'The disposable child report is pending.'
                pending.set()
        else:
            phase = 'parent-spawn'
            pending.clear()
            release_child.clear()
            content = None
            tool_calls = [{'id':'synthetic-spawn-1','type':'function','function':{'name':'spawn_agent','arguments':json.dumps({'agent':'synthetic-reader','task':'Return a harmless arithmetic report for 2 + 2.','_rationale':'Run the explicitly requested disposable arithmetic acceptance task.'})}}]
        if args.events:
            with args.events.open('a') as out:
                out.write(json.dumps({'phase':phase,'at':time.time(),'utc':datetime.now(timezone.utc).isoformat(),
                                      'marker':marker,'roles':[m.get('role') for m in messages]})+'\n')
        if body.get('stream'):
            self.send_response(200)
            self.send_header('Content-Type','text/event-stream')
            self.end_headers()
            delta = {'role':'assistant'}
            if content is not None:
                delta['content'] = content
            if tool_calls:
                delta['tool_calls'] = [dict(index=i, **t) for i,t in enumerate(tool_calls)]
            for value in [
                {'id':'fixture','object':'chat.completion.chunk','model':'synthetic-native-acceptance','choices':[{'index':0,'delta':delta,'finish_reason':None}]},
                {'id':'fixture','object':'chat.completion.chunk','model':'synthetic-native-acceptance','choices':[{'index':0,'delta':{},'finish_reason':'tool_calls' if tool_calls else 'stop'}],'usage':{'prompt_tokens':64,'completion_tokens':16,'total_tokens':80}},
            ]:
                self.wfile.write(('data: '+json.dumps(value)+'\n\n').encode())
            self.wfile.write(b'data: [DONE]\n\n')
            self.wfile.flush()
        else:
            message={'role':'assistant','content':content}
            if tool_calls:
                message['tool_calls']=tool_calls
            self.json_response({'id':'fixture','object':'chat.completion','model':'synthetic-native-acceptance','choices':[{'index':0,'message':message,'finish_reason':'tool_calls' if tool_calls else 'stop'}],'usage':{'prompt_tokens':64,'completion_tokens':16,'total_tokens':80}})

ThreadingHTTPServer(('127.0.0.1',args.port),Handler).serve_forever()
