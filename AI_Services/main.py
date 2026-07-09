import lancedb
from fastapi import FastAPI
from pydantic import BaseModel
from openai import OpenAI

app = FastAPI()
client = OpenAI()

db = lancedb.connect("./lavender_vectors")
TABLE_NAME = "code_chunks"
EMBED_MODEL = "text-embedding-3-small"


class CodeChunk(BaseModel):
    id: str
    file_path: str
    chunk_type: str
    namespace: str = ""
    class_name: str = ""
    member_name: str = ""
    signature: str = ""
    start_line: int = 0
    end_line: int = 0
    code: str
    embedding_text: str


class EmbedProjectRequest(BaseModel):
    chunks: list[CodeChunk]


class SearchRequest(BaseModel):
    query: str
    top_k: int = 5


def get_embedding(text: str) -> list[float]:
    response = client.embeddings.create(
        model=EMBED_MODEL,
        input=text
    )
    return response.data[0].embedding


@app.post("/embed-project")
def embed_project(request: EmbedProjectRequest):
    rows = []

    for chunk in request.chunks:
        rows.append({
            "id": chunk.id,
            "file_path": chunk.file_path,
            "chunk_type": chunk.chunk_type,
            "namespace": chunk.namespace,
            "class_name": chunk.class_name,
            "member_name": chunk.member_name,
            "signature": chunk.signature,
            "start_line": chunk.start_line,
            "end_line": chunk.end_line,
            "code": chunk.code,
            "embedding_text": chunk.embedding_text,
            "vector": get_embedding(chunk.embedding_text),
        })

    if TABLE_NAME in db.table_names():
        db.drop_table(TABLE_NAME)

    db.create_table(TABLE_NAME, rows)

    return {
        "message": "Project embedded",
        "chunk_count": len(rows)
    }


@app.post("/search")
def search(request: SearchRequest):
    table = db.open_table(TABLE_NAME)
    query_vector = get_embedding(request.query)

    results = (
        table.search(query_vector)
        .limit(request.top_k)
        .to_list()
    )

    return {
        "query": request.query,
        "results": results
    }