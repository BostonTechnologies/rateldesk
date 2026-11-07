"""Disposable loopback model; real Netclaw owns actors, sessions and tools.

Run only one continuation smoke at a time. The receiver releases child work
after observing its durable pending state; no timing sleep establishes success.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import argparse
import json
import threading
import time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--port', type=int, default=19598)
parser.add_argument('--events', type=Path, help='Optional phase/role receipt file; no message content is logged.')
args = parser.parse_args()
pending = threading.Event()
release_child = threading.Event()

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
        if self.path == '/fixture/release-child':
            release_child.set()
            return self.json_response({'released':True})
        body = json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))))
        messages = body.get('messages', [])
        systems = '\n'.join(str(m.get('content','')) for m in messages if m.get('role') == 'system')
        tool_calls = None
        if not body.get('tools'):
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
                out.write(json.dumps({'phase':phase,'at':time.time(),'roles':[m.get('role') for m in messages]})+'\n')
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
