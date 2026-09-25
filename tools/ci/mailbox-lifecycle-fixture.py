#!/usr/bin/env python3
"""Talk only to the loopback GreenMail fixture used by mailbox lifecycle acceptance."""

import argparse
import base64
import email
import html
import imaplib
import json
import os
import smtplib
import ssl
import uuid
from email.message import EmailMessage
from email.policy import default


ACCOUNTS = {
    "support": ("support@tenant-a.example.test", "synthetic-mail-password", "primary"),
    "requester": ("requester@tenant-a.example.test", "synthetic-mail-password", "primary"),
    "recipient": ("recipient@tenant-a.example.test", "synthetic-mail-password", "primary"),
    "global": ("global@tenant-b.example.test", "synthetic-global-password", "global"),
    "requester-b": ("requester@tenant-b.example.test", "synthetic-global-password", "global"),
    "dedicated-b": ("dedicated-b@tenant-b.example.test", "synthetic-dedicated-b-password", "global"),
    "pop": ("pop@tenant-c.example.test", "synthetic-pop-password", "primary"),
    "requester-c": ("requester@tenant-c.example.test", "synthetic-pop-password", "primary"),
}


def address(local_part: str) -> str:
    if local_part not in ACCOUNTS:
        raise ValueError("Only synthetic fixture accounts are allowed")
    return ACCOUNTS[local_part][0]


def connection_context() -> ssl.SSLContext:
    return ssl.create_default_context(cafile=os.environ["MAILBOX_FIXTURE_CA"])


def send(args: argparse.Namespace) -> None:
    sender = address(args.sender)
    recipient = address(args.recipient)
    _, password, endpoint = ACCOUNTS[args.sender]
    message = EmailMessage()
    message["From"] = sender
    message["To"] = recipient
    message["Subject"] = args.subject
    message["Message-ID"] = f"<{uuid.uuid4().hex}@{sender.split('@')[1]}>"
    if args.in_reply_to:
        message["In-Reply-To"] = args.in_reply_to
        message["References"] = args.in_reply_to
    if args.auto_submitted:
        message["Auto-Submitted"] = args.auto_submitted
    message.set_content(args.body)
    if args.rich_mime:
        message.add_alternative(
            f'<p>{html.escape(args.body)}</p><img src="cid:fixture-inline">', subtype="html"
        )
        message.get_payload()[1].add_related(
            base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lXcAAAAASUVORK5CYII="),
            maintype="image", subtype="png", cid="<fixture-inline>", filename="fixture-inline.png"
        )
        message.add_attachment(
            f"Attachment for {args.subject}\n".encode("utf-8"),
            maintype="text", subtype="plain", filename="fixture-note.txt"
        )
        nested = EmailMessage()
        nested["From"] = "nested@tenant-a.example.test"
        nested["To"] = recipient
        nested["Subject"] = "Nested fixture evidence"
        nested.set_content("Nested fixture body")
        message.add_attachment(nested, filename="nested-evidence.eml")
    port = "MAILBOX_FIXTURE_GLOBAL_SMTPS_PORT" if endpoint == "global" else "MAILBOX_FIXTURE_SMTPS_PORT"
    with smtplib.SMTP_SSL("localhost", int(os.environ[port]),
                          context=connection_context(), timeout=20) as smtp:
        smtp.login(sender, password)
        smtp.send_message(message)
    print(json.dumps({"messageId": message["Message-ID"]}))


def messages(args: argparse.Namespace) -> None:
    account = address(args.account)
    _, password, endpoint = ACCOUNTS[args.account]
    port = "MAILBOX_FIXTURE_GLOBAL_IMAPS_PORT" if endpoint == "global" else "MAILBOX_FIXTURE_IMAPS_PORT"
    with imaplib.IMAP4_SSL("localhost", int(os.environ[port]),
                           ssl_context=connection_context(), timeout=20) as imap:
        imap.login(account, password)
        status, _ = imap.select("INBOX", readonly=True)
        if status != "OK":
            raise RuntimeError("Fixture INBOX could not be opened")
        status, ids = imap.search(None, "ALL")
        if status != "OK":
            raise RuntimeError("Fixture INBOX could not be searched")
        result = []
        for message_id in ids[0].split():
            status, parts = imap.fetch(message_id, "(RFC822)")
            if status != "OK":
                raise RuntimeError("Fixture message could not be fetched")
            raw = next(part[1] for part in parts if isinstance(part, tuple))
            parsed = email.message_from_bytes(raw, policy=default)
            result.append({
                "subject": str(parsed["Subject"] or ""),
                "from": str(parsed["From"] or ""),
                "to": str(parsed["To"] or ""),
                "replyTo": str(parsed["Reply-To"] or ""),
                "messageId": str(parsed["Message-ID"] or ""),
                "body": parsed.get_body(preferencelist=("plain", "html")).get_content()
                if parsed.get_body(preferencelist=("plain", "html")) else "",
            })
    print(json.dumps(result))


def main() -> None:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    send_command = commands.add_parser("send")
    send_command.add_argument("--sender", required=True)
    send_command.add_argument("--recipient", required=True)
    send_command.add_argument("--subject", required=True)
    send_command.add_argument("--body", required=True)
    send_command.add_argument("--in-reply-to", default="")
    send_command.add_argument("--auto-submitted", choices=("auto-replied", "auto-generated"))
    send_command.add_argument("--rich-mime", action="store_true")
    list_command = commands.add_parser("messages")
    list_command.add_argument("--account", required=True)
    args = parser.parse_args()
    if args.command == "send":
        send(args)
    else:
        messages(args)


if __name__ == "__main__":
    main()
