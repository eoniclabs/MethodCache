#!/usr/bin/env python3
"""
GitHub PR Comment Manager — AI/Batch Friendly
============================================

SETUP:
1) export GITHUB_TOKEN="<token>"  (classic: repo OR fine‑grained: Pull requests: write)
2) Run commands below. For GraphQL resolve, same token is used.

KEY CONCEPTS:
- Issue Comments: General PR discussion (no true threading)
- Review Comments: Code comments (threaded). Replies appear at thread level.
- Comment IDs: Use IDs or a comment URL ending with #discussion_r<ID>.

WORKFLOWS:
- Explore:  pr_comments.py get <owner> <repo> <pr>
- Single reply: pr_comments.py reply <owner> <repo> <pr> <comment_id> "text"
- Your comments: pr_comments.py my-comments <owner> <repo> <pr>
- Batch replies: pr_comments.py reply-batch <json_path> [--dry-run]

BATCH MODE (reply-batch):
- Input JSON contains meta (owner/repo/pr), optional policy, and replies[].
- Idempotency via hidden HTML marker in each reply to avoid duplicates.
- Optional: per-item/default reactions and thread resolution (GraphQL).

NOTE: Replying to review sub-comments is supported. The reply posts at the
thread level using the same in_reply_to semantics and works for sub-comments.
"""

import os
import sys
import json
import re
import hashlib
import time
import subprocess
import requests
from datetime import datetime

# --- REST/GQL endpoints for new batch features ---
REST = "https://api.github.com"
GQL = "https://api.github.com/graphql"

# Resolve GitHub token from env or gh CLI (fallback)
def _resolve_token() -> str:
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN')
    if token:
        return token
    # Fallback: try gh auth token (will not print token)
    try:
        out = subprocess.run([
            'gh', 'auth', 'token'
        ], capture_output=True, text=True, check=True)
        gh_token = out.stdout.strip()
        if gh_token:
            return gh_token
    except Exception:
        pass
    return ''

TOKEN = _resolve_token()
if not TOKEN:
    print("Error: No GitHub token found. Set GITHUB_TOKEN or run 'gh auth login'.")
    sys.exit(1)

HEADERS = {
    "Authorization": f"token {TOKEN}",
    "Accept": "application/vnd.github.v3+json"
}

# New-style headers for REST calls used by batch mode
def gh_headers():
    tok = TOKEN
    if not tok:
        raise SystemExit("GITHUB_TOKEN is required")
    return {
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {tok}",
        "X-GitHub-Api-Version": "2022-11-28",
    }

# --- hidden idempotency marker helpers ---
def _marker(fp: str, parent_id: int) -> str:
    return f"<!-- moliri-reply:{fp}:{parent_id} -->"

def _list_review_comments(owner: str, repo: str, pr: int):
    """Yield all review comments for a PR (paginated)."""
    url = f"{REST}/repos/{owner}/{repo}/pulls/{pr}/comments?per_page=100"
    while url:
        r = requests.get(url, headers=gh_headers())
        r.raise_for_status()
        for c in r.json():
            yield c
        url = r.links.get("next", {}).get("url")
        if url:
            time.sleep(0.1)

def _already_replied(owner: str, repo: str, pr: int, parent_id: int, fp: str) -> bool:
    m = _marker(fp, parent_id)
    for c in _list_review_comments(owner, repo, pr):
        # Check replies in the same thread for the marker
        if c.get("in_reply_to_id") == parent_id and m in (c.get("body") or ""):
            return True
    return False

def _parse_comment_ref(item) -> int:
    """Return numeric review comment ID from comment_id or from a #discussion_r<ID> URL."""
    if "comment_id" in item and item["comment_id"] is not None:
        return int(item["comment_id"])
    m = re.search(r"#discussion_r(\d+)$", item.get("comment", ""))
    if not m:
        raise ValueError("Provide comment_id or a comment URL ending with #discussion_r<ID>")
    return int(m.group(1))

def _reply_via_in_reply_to(owner: str, repo: str, pr: int, parent_id: int, body: str):
    payload = {"in_reply_to": parent_id, "body": body}
    r = requests.post(
        f"{REST}/repos/{owner}/{repo}/pulls/{pr}/comments",
        headers=gh_headers(),
        json=payload,
    )
    r.raise_for_status()
    return r.json()

def _react_to_review_comment(owner: str, repo: str, parent_id: int, emoji: str):
    url = f"{REST}/repos/{owner}/{repo}/pulls/comments/{parent_id}/reactions"
    r = requests.post(url, headers=gh_headers(), json={"content": emoji})
    # 200/201 success; 422 means reaction already exists
    if r.status_code not in (200, 201, 422):
        r.raise_for_status()

def _gql(query: str, variables: dict):
    h = {"Authorization": f"Bearer {TOKEN}"}
    r = requests.post(GQL, json={"query": query, "variables": variables}, headers=h)
    r.raise_for_status()
    js = r.json()
    if "errors" in js:
        raise RuntimeError(js["errors"])  # bubble up
    return js["data"]

def _get_thread_id_from_comment_node(node_id: str):
    q = """
    query($id: ID!) {
      node(id: $id) {
        ... on PullRequestReviewComment {
          pullRequestReviewThread { id isResolved }
        }
      }
    }
    """
    data = _gql(q, {"id": node_id})
    thr = data["node"]["pullRequestReviewThread"]
    return thr["id"], thr["isResolved"]

def _resolve_thread(thread_id: str):
    q = """
    mutation($threadId: ID!) {
      resolveReviewThread(input:{threadId: $threadId}) {
        thread { id isResolved }
      }
    }
    """
    _gql(q, {"threadId": thread_id})

def reply_batch_from_json(path: str, prefer: str = "auto", dry_run: bool = False) -> None:
    """
    Batch reply to PR review comments using a single JSON “reply package”.

    JSON contract:
      {
        "meta": {"owner": "ORG", "repo": "REPO", "pr": 123},
        "policy": {"confirm": true, "auto_resolve": false, "react_default": "+1", "fail_on_error": false},
        "replies": [ {"comment_id": 123456789, "body": "...", "react": "eyes", "resolve": true}, ... ]
      }

    Idempotency:
      Appends hidden marker <!-- moliri-reply:<fp>:<parentId> --> to each reply.
      Scans existing thread replies to skip duplicates.
    """
    try:
        with open(path, "r", encoding="utf-8") as fh:
            spec = json.load(fh)
    except Exception as e:
        raise SystemExit(f"Failed to read JSON: {e}")

    # validate
    try:
        owner = spec["meta"]["owner"]
        repo = spec["meta"]["repo"]
        pr = int(spec["meta"]["pr"])
        replies = spec.get("replies", [])
        if not isinstance(replies, list) or not replies:
            raise ValueError("'replies' must be a non-empty array")
    except Exception as e:
        raise SystemExit(f"Invalid JSON spec: {e}")

    policy = spec.get("policy", {})
    fp = hashlib.sha256(json.dumps(spec, sort_keys=True).encode("utf-8")).hexdigest()[:12]

    # preview
    print(f"Batch replies for {owner}/{repo} PR #{pr} | items: {len(replies)}")
    try:
        sample_parent = _parse_comment_ref(replies[0])
        sample_body = (replies[0].get("body") or "").strip().splitlines()[0:1]
        sample_body = sample_body[0] if sample_body else ""
        print(f"Sample: parent {sample_parent} | '{sample_body[:80]}'")
    except Exception:
        pass

    if policy.get("confirm", True) and not dry_run:
        ok = input("Proceed? [y/N] ").strip().lower() == "y"
        if not ok:
            print("Aborted.")
            return

    had_error = False

    for item in replies:
        try:
            parent_id = _parse_comment_ref(item)
        except Exception as e:
            print(f"REPLY ?: error {e}")
            had_error = True
            if policy.get("fail_on_error"):
                break
            continue

        body = (item.get("body") or "").rstrip() + "\n\n" + _marker(fp, parent_id)
        react = item.get("react") or policy.get("react_default")
        want_resolve = bool(item.get("resolve") or policy.get("auto_resolve"))

        # idempotency marker scan
        try:
            if _already_replied(owner, repo, pr, parent_id, fp):
                print(f"REPLY {parent_id}: skipped (marker)")
                # still react/resolve? spec implies actions apply after reply; skip both to be safe
                continue
        except Exception as e:
            print(f"REPLY {parent_id}: error during marker scan: {e}")
            had_error = True
            if policy.get("fail_on_error"):
                break
            continue

        # post reply
        if dry_run:
            print(f"REPLY {parent_id}: DRY RUN")
        else:
            try:
                # Preserve current semantics: in_reply_to works for sub-comments
                reply_json = _reply_via_in_reply_to(owner, repo, pr, parent_id, body)
                new_id = reply_json.get("id")
                print(f"REPLY {parent_id}: ok {new_id}")
            except Exception as e:
                print(f"REPLY {parent_id}: error {e}")
                had_error = True
                if policy.get("fail_on_error"):
                    break

        # reactions (apply to parent comment)
        if react:
            if dry_run:
                print(f"REACT {parent_id}: DRY RUN ({react})")
            else:
                try:
                    _react_to_review_comment(owner, repo, parent_id, react)
                    # Cannot distinguish 422 easily without parsing body; treat as ok/exists neutral
                    print(f"REACT {parent_id}: ok")
                except requests.HTTPError as he:
                    # 422 already reacted
                    if he.response is not None and he.response.status_code == 422:
                        print(f"REACT {parent_id}: exists")
                    else:
                        print(f"REACT {parent_id}: error {he}")
                        had_error = True
                        if policy.get("fail_on_error"):
                            break
                except Exception as e:
                    print(f"REACT {parent_id}: error {e}")
                    had_error = True
                    if policy.get("fail_on_error"):
                        break

        # resolve thread (GraphQL)
        if want_resolve:
            if dry_run:
                print(f"RESOLVE {parent_id}: DRY RUN")
            else:
                try:
                    pc = requests.get(
                        f"{REST}/repos/{owner}/{repo}/pulls/comments/{parent_id}",
                        headers=gh_headers(),
                    )
                    pc.raise_for_status()
                    node_id = pc.json()["node_id"]
                    thread_id, is_resolved = _get_thread_id_from_comment_node(node_id)
                    if not is_resolved:
                        _resolve_thread(thread_id)
                        print(f"RESOLVE thread {thread_id}: ok")
                    else:
                        print(f"RESOLVE thread {thread_id}: already")
                except Exception as e:
                    print(f"RESOLVE {parent_id}: error {e}")
                    had_error = True
                    if policy.get("fail_on_error"):
                        break

    if had_error and policy.get("fail_on_error"):
        sys.exit(1)

def get_pr_comments(owner, repo, pr_number):
    """
    Get all comments from a PR (both issue comments and review comments)
    
    FOR AI AGENTS:
    - Returns structured JSON saved to: pr_{owner}_{repo}_{pr_number}_comments.json
    - Issue comments: Have unique IDs like 98765 for replies
    - Review comments: Organized in threads with root and sub-comments
    - Outdated comments: Marked with "outdated": true (from old code versions)
    - Use comment IDs from output to reply to specific comments
    """
    base_url = "https://api.github.com"
    
    print(f"\n📋 Fetching comments for {owner}/{repo} PR #{pr_number}\n")
    
    # Get PR details first to understand state
    pr_url = f"{base_url}/repos/{owner}/{repo}/pulls/{pr_number}"
    pr_response = requests.get(pr_url, headers=HEADERS)
    pr_response.raise_for_status()
    pr_data = pr_response.json()
    
    is_merged = pr_data.get('merged', False)
    is_closed = pr_data.get('state') == 'closed'
    
    # Get issue comments (general discussion) with pagination
    issue_comments = []
    issue_url = f"{base_url}/repos/{owner}/{repo}/issues/{pr_number}/comments?per_page=100"
    while issue_url:
        issue_response = requests.get(issue_url, headers=HEADERS)
        issue_response.raise_for_status()
        issue_comments.extend(issue_response.json())
        issue_url = issue_response.links.get("next", {}).get("url")
        if issue_url:
            time.sleep(0.1)
    
    # Get review comments (code-specific) with pagination
    review_comments = []
    review_url = f"{base_url}/repos/{owner}/{repo}/pulls/{pr_number}/comments?per_page=100"
    while review_url:
        review_response = requests.get(review_url, headers=HEADERS)
        review_response.raise_for_status()
        review_comments.extend(review_response.json())
        review_url = review_response.links.get("next", {}).get("url")
        if review_url:
            time.sleep(0.1)
    
    # Get review threads to check if resolved
    reviews_url = f"{base_url}/repos/{owner}/{repo}/pulls/{pr_number}/reviews"
    reviews_response = requests.get(reviews_url, headers=HEADERS)
    reviews_response.raise_for_status()
    reviews = reviews_response.json()
    
    # Structure the output
    output = {
        "pr": {
            "owner": owner,
            "repo": repo,
            "number": pr_number,
            "state": pr_data['state'],
            "merged": is_merged,
            "title": pr_data['title']
        },
        "summary": {
            "total_issue_comments": len(issue_comments),
            "total_review_comments": len(review_comments),
            "total_all": len(issue_comments) + len(review_comments)
        },
        "issue_comments": [],
        "review_comments": []
    }
    
    # Process issue comments
    for comment in issue_comments:
        output["issue_comments"].append({
            "id": comment['id'],
            "author": comment['user']['login'],
            "body": comment['body'],
            "created_at": comment['created_at'],
            "html_url": comment['html_url'],
            "type": "issue_comment"
        })
    
    # Process review comments with thread info
    review_threads = {}
    for comment in review_comments:
        thread_id = comment.get('in_reply_to_id') or comment['id']
        
        if thread_id not in review_threads:
            review_threads[thread_id] = {
                "id": thread_id,
                "path": comment['path'],
                "line": comment.get('line'),
                "resolved": False,  # GitHub API doesn't directly expose this
                "outdated": comment.get('position') is None,  # If position is null, comment is outdated
                "comments": []
            }
        
        review_threads[thread_id]["comments"].append({
            "id": comment['id'],
            "author": comment['user']['login'],
            "body": comment['body'],
            "created_at": comment['created_at'],
            "html_url": comment['html_url'],
            "in_reply_to": comment.get('in_reply_to_id')
        })
    
    output["review_comments"] = list(review_threads.values())
    
    # Print summary with AI agent instructions
    print("=" * 60)
    print(f"PR #{pr_number}: {pr_data['title']}")
    print(f"State: {pr_data['state']} {'(merged)' if is_merged else ''}")
    print("=" * 60)
    
    print(f"\n📝 Issue Comments: {len(issue_comments)} (use IDs below for replies)")
    for comment in output["issue_comments"]:
        preview = comment['body'][:80].replace('\n', ' ')
        if len(comment['body']) > 80:
            preview += "..."
        print(f"  [{comment['id']}] @{comment['author']}: {preview}")
    
    print(f"\n💻 Review Comments: {len(review_threads)} threads")
    print("  Note: All replies appear at same level (no deep nesting)")
    for thread in output["review_comments"]:
        status = "❌ OUTDATED" if thread['outdated'] else "✅ ACTIVE"
        print(f"  Thread on {thread['path']} {status}")
        
        # Sort comments to show thread structure
        root_comments = [c for c in thread['comments'] if not c['in_reply_to']]
        sub_comments = [c for c in thread['comments'] if c['in_reply_to']]
        
        for comment in root_comments:
            preview = comment['body'][:60].replace('\n', ' ')
            if len(comment['body']) > 60:
                preview += "..."
            print(f"    • [{comment['id']}] @{comment['author']}: {preview}")
            
            # Show sub-comments under their parent
            for sub in sub_comments:
                if sub['in_reply_to'] == comment['id']:
                    sub_preview = sub['body'][:60].replace('\n', ' ')
                    if len(sub['body']) > 60:
                        sub_preview += "..."
                    print(f"      └─ [{sub['id']}] @{sub['author']}: {sub_preview}")
    
    # Save to file for reference
    output_file = f"pr_{owner}_{repo}_{pr_number}_comments.json"
    with open(output_file, 'w') as f:
        json.dump(output, f, indent=2)
    
    print(f"\n💾 Full data saved to: {output_file}")
    print(f"📋 AI AGENTS: Parse the JSON file to process comments programmatically")
    print(f"✨ Use comment IDs above with 'reply' command to respond to specific comments")
    
    return output

def reply_to_comment(owner, repo, pr_number, comment_id, reply_text, comment_type="auto"):
    """
    Reply to a specific comment (handles both issue and review comments)
    
    FOR AI AGENTS:
    - comment_id: The numeric ID from the 'get' command output
    - comment_type: Usually leave as "auto" for automatic detection
    - Issue comments: Creates new comment mentioning original author
    - Review comments: Adds to thread (all replies at same level, no deep nesting)
    - Returns: True if successful, False if failed
    
    THREADING BEHAVIOR:
    - You CAN reply to any comment (root or sub-comment)
    - All replies appear at the same thread level (no deep nesting)
    - When replying to sub-comments, your reply appears alongside it, not under it
    """
    base_url = "https://api.github.com"
    
    print(f"\n💬 Replying to comment {comment_id} in {owner}/{repo} PR #{pr_number}")
    
    # If auto-detect, try to figure out comment type
    if comment_type == "auto":
        # Try issue comment first
        check_url = f"{base_url}/repos/{owner}/{repo}/issues/comments/{comment_id}"
        response = requests.get(check_url, headers=HEADERS)
        
        if response.status_code == 200:
            comment_type = "issue"
            print("   Type: Issue comment")
        else:
            comment_type = "review"
            print("   Type: Review comment")
    
    try:
        if comment_type == "issue":
            # GitHub doesn't support true threading for issue comments
            # We'll mention the author and quote part of their comment for context
            author = get_comment_author(owner, repo, comment_id, 'issue')
            
            # Get original comment for context
            comment_url = f"{base_url}/repos/{owner}/{repo}/issues/comments/{comment_id}"
            comment_response = requests.get(comment_url, headers=HEADERS)
            
            if comment_response.status_code == 200:
                original = comment_response.json()
                original_snippet = original['body'][:100]
                if len(original['body']) > 100:
                    original_snippet += "..."
                
                # Format reply with context
                body = f"@{author} (replying to [your comment](#{comment_id}))\n\n> {original_snippet}\n\n{reply_text}"
            else:
                body = f"@{author} {reply_text}"
            
            url = f"{base_url}/repos/{owner}/{repo}/issues/{pr_number}/comments"
            response = requests.post(url, headers=HEADERS, json={"body": body})
            
        else:  # review comment
            # First, check if this comment already has a parent (is it already a sub-comment?)
            comment_url = f"{base_url}/repos/{owner}/{repo}/pulls/comments/{comment_id}"
            comment_response = requests.get(comment_url, headers=HEADERS)
            
            if comment_response.status_code == 200:
                original = comment_response.json()
                
                # If this comment has in_reply_to_id, it's already a sub-comment
                # GitHub API doesn't support replies to replies - must reply to thread root
                if original.get('in_reply_to_id'):
                    print(f"   Note: Replying to sub-comment in thread {original['in_reply_to_id']}")
                    print(f"   (Reply will appear at same level as other replies)")
                    # When replying to a sub-comment, still use the sub-comment ID
                    # GitHub will add the reply to the thread at the same level
                    url = f"{base_url}/repos/{owner}/{repo}/pulls/{pr_number}/comments"
                    payload = {
                        "body": f"@{original['user']['login']} {reply_text}",
                        "in_reply_to": comment_id  # Can use the sub-comment ID
                    }
                    response = requests.post(url, headers=HEADERS, json=payload)
                else:
                    # This is a root comment, we can reply to it
                    print(f"   Note: Replying to thread root comment")
                    
                    # Use the standard in_reply_to method
                    url = f"{base_url}/repos/{owner}/{repo}/pulls/{pr_number}/comments"
                    payload = {
                        "body": reply_text,
                        "in_reply_to": comment_id
                    }
                    response = requests.post(url, headers=HEADERS, json=payload)
            else:
                print(f"❌ Could not fetch original comment details")
                return False
        
        if response.status_code in [200, 201]:
            result = response.json()
            print(f"✅ Reply posted successfully!")
            print(f"   URL: {result['html_url']}")
            print(f"   Reply ID: {result['id']} (use this ID to reply to this sub-comment)")
            return True
        else:
            print(f"❌ Failed to post reply: {response.status_code}")
            print(f"   Response: {response.text}")
            return False
            
    except Exception as e:
        print(f"❌ Error posting reply: {str(e)}")
        return False

def get_comment_author(owner, repo, comment_id, comment_type):
    """Helper to get comment author for mentions"""
    base_url = "https://api.github.com"
    
    if comment_type == "issue":
        url = f"{base_url}/repos/{owner}/{repo}/issues/comments/{comment_id}"
    else:
        url = f"{base_url}/repos/{owner}/{repo}/pulls/comments/{comment_id}"
    
    response = requests.get(url, headers=HEADERS)
    if response.status_code == 200:
        return response.json()['user']['login']
    return ""

def list_my_pr_comments(owner, repo, pr_number):
    """
    List all comments made by the current user
    
    FOR AI AGENTS:
    - Use this to avoid duplicate replies
    - Shows all your previous comments with their IDs
    - Helps track which comments you've already responded to
    """
    base_url = "https://api.github.com"
    
    # Get current user
    user_response = requests.get(f"{base_url}/user", headers=HEADERS)
    user_response.raise_for_status()
    my_username = user_response.json()['login']
    
    print(f"\n👤 Finding comments by @{my_username} in {owner}/{repo} PR #{pr_number}\n")
    
    # Get all comments
    all_comments = get_pr_comments(owner, repo, pr_number)
    
    my_comments = []
    
    # Filter issue comments
    for comment in all_comments['issue_comments']:
        if comment['author'] == my_username:
            my_comments.append(comment)
            print(f"Issue comment [{comment['id']}]: {comment['body'][:100]}...")
    
    # Filter review comments
    for thread in all_comments['review_comments']:
        for comment in thread['comments']:
            if comment['author'] == my_username:
                my_comments.append(comment)
                print(f"Review comment [{comment['id']}]: {comment['body'][:100]}...")
    
    print(f"\n📊 Total: {len(my_comments)} comments by you")
    return my_comments

def main():
    """
    Simple CLI interface for AI agent interaction
    
    EXAMPLES FOR AI AGENTS:
    1. Get all comments and save to JSON:
       python pr_comments.py get microsoft vscode 12345
       
    2. Reply to a specific comment:
       python pr_comments.py reply microsoft vscode 12345 987654321 "Thanks for the feedback!"
       
    3. Check what you've already replied to:
       python pr_comments.py my-comments microsoft vscode 12345
    
    OUTPUT:
    - 'get' command: Saves JSON file and prints summary with comment IDs
    - 'reply' command: Returns success/failure status
    - 'my-comments': Lists your previous replies to avoid duplicates
    """
    if len(sys.argv) < 2:
        print("""
GitHub PR Comment Tool - AI Agent Instructions
==============================================

SETUP:
  export GITHUB_TOKEN=your_github_token

USAGE:
  pr_comments.py get <owner> <repo> <pr_number>
    Get all comments from a PR and save to JSON file
    Output shows comment IDs needed for replies
    
  pr_comments.py reply <owner> <repo> <pr_number> <comment_id> "reply text"
    Reply to a specific comment using its ID
    Handles both issue and review comments automatically
    
  pr_comments.py my-comments <owner> <repo> <pr_number>
    List all your previous comments to avoid duplicates

  pr_comments.py reply-batch <json_path> [--dry-run]
    Post many replies from one JSON package with idempotency, optional
    reactions, and optional thread resolution via GraphQL. If the JSON
    has policy.confirm=true, you'll get a single approval prompt.

EXAMPLES:
  pr_comments.py get microsoft vscode 12345
  pr_comments.py reply microsoft vscode 12345 987654321 "Thanks for the feedback!"
  pr_comments.py my-comments microsoft vscode 12345
  pr_comments.py reply-batch pr-replies.json --dry-run

AI AGENT WORKFLOW:
  1. Run 'get' to fetch all comments and save JSON
  2. Parse pr_owner_repo_number_comments.json
  3. Use comment IDs to reply to specific comments
  4. Run 'my-comments' to track what you've replied to
  5. For bulk replies, prepare pr-replies.json and run reply-batch

IMPORTANT NOTES:
  - Issue comments: No threading, mentions author in reply
  - Review comments: Threading supported; replies post at thread level
  - Sub-comments: Replying to a sub-comment works using in_reply_to

JSON CONTRACT (reply-batch brief):
{
  "meta": {"owner":"ORG","repo":"REPO","pr":123},
  "policy": {"confirm": true, "auto_resolve": false, "react_default": "+1", "fail_on_error": false},
  "replies": [
    {"comment": "https://github.com/ORG/REPO/pull/123#discussion_r104567890", "body": "Fixed.", "react":"eyes", "resolve": true},
    {"comment_id": 987654321, "body": "Filed follow-up: #456", "resolve": false}
  ]
}
        """)
        sys.exit(1)
    
    command = sys.argv[1]
    
    if command == "get":
        if len(sys.argv) < 5:
            print("Usage: pr_comments.py get <owner> <repo> <pr_number>")
            sys.exit(1)
        
        owner = sys.argv[2]
        repo = sys.argv[3]
        pr_number = int(sys.argv[4])
        
        get_pr_comments(owner, repo, pr_number)
    
    elif command == "reply":
        if len(sys.argv) < 7:
            print("Usage: pr_comments.py reply <owner> <repo> <pr_number> <comment_id> \"reply text\"")
            sys.exit(1)
        
        owner = sys.argv[2]
        repo = sys.argv[3]
        pr_number = int(sys.argv[4])
        comment_id = int(sys.argv[5])
        reply_text = sys.argv[6]
        
        reply_to_comment(owner, repo, pr_number, comment_id, reply_text)
    
    elif command == "my-comments":
        if len(sys.argv) < 5:
            print("Usage: pr_comments.py my-comments <owner> <repo> <pr_number>")
            sys.exit(1)
        
        owner = sys.argv[2]
        repo = sys.argv[3]
        pr_number = int(sys.argv[4])
        
        list_my_pr_comments(owner, repo, pr_number)
    
    elif command == "reply-batch":
        if len(sys.argv) < 3:
            print("Usage: pr_comments.py reply-batch <json_path> [--dry-run]")
            sys.exit(1)
        json_path = sys.argv[2]
        dry = "--dry-run" in sys.argv[3:]
        reply_batch_from_json(json_path, prefer="auto", dry_run=dry)
    
    else:
        print(f"Unknown command: {command}")
        print("Use 'get', 'reply', or 'my-comments'")
        sys.exit(1)

if __name__ == "__main__":
    main()
