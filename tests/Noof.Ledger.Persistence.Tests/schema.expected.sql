CREATE TABLE public.app_secret (
    key text NOT NULL,
    ciphertext text NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT "PK_app_secret" PRIMARY KEY (key)
);


CREATE TABLE public.app_user (
    id uuid NOT NULL,
    username character varying(64) NOT NULL,
    password_hash text NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT "PK_app_user" PRIMARY KEY (id)
);


CREATE TABLE public.backup_runs (
    id uuid NOT NULL,
    started_at timestamptz NOT NULL,
    finished_at timestamptz NOT NULL,
    succeeded boolean NOT NULL,
    file_name text,
    size_bytes bigint,
    error text,
    CONSTRAINT "PK_backup_runs" PRIMARY KEY (id)
);


CREATE TABLE public.categories (
    id uuid NOT NULL,
    parent_id uuid,
    slug character varying(64) NOT NULL,
    name_en character varying(128) NOT NULL,
    name_ru character varying(128) NOT NULL,
    is_active boolean NOT NULL,
    CONSTRAINT "PK_categories" PRIMARY KEY (id),
    CONSTRAINT "FK_categories_categories_parent_id" FOREIGN KEY (parent_id) REFERENCES public.categories (id) ON DELETE RESTRICT
);


CREATE TABLE public.merchants (
    id uuid NOT NULL,
    display_name character varying(256) NOT NULL,
    kind integer NOT NULL,
    CONSTRAINT "PK_merchants" PRIMARY KEY (id)
);


CREATE TABLE public.wallets (
    id uuid NOT NULL,
    name character varying(128) NOT NULL,
    currency character varying(3) NOT NULL,
    aliases text[] NOT NULL DEFAULT ('{}'),
    is_default_for_currency boolean NOT NULL,
    archived boolean NOT NULL DEFAULT FALSE,
    created_at timestamptz NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_wallets" PRIMARY KEY (id)
);


CREATE TABLE public.merchant_aliases (
    folded character varying(256) NOT NULL,
    merchant_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT "PK_merchant_aliases" PRIMARY KEY (folded),
    CONSTRAINT "FK_merchant_aliases_merchants_merchant_id" FOREIGN KEY (merchant_id) REFERENCES public.merchants (id) ON DELETE RESTRICT
);


CREATE TABLE public.transactions (
    id uuid NOT NULL,
    wallet_id uuid,
    kind integer NOT NULL,
    raw_text text,
    capture_kind integer NOT NULL,
    voice_file_id text,
    voice_duration_seconds integer,
    status integer NOT NULL,
    time_zone_id character varying(64) NOT NULL,
    occurred_at timestamptz NOT NULL,
    occurred_on date NOT NULL,
    telegram_chat_id bigint,
    telegram_message_id integer,
    bot_message_id integer,
    prompt_message_id integer,
    created_at timestamptz NOT NULL,
    CONSTRAINT "PK_transactions" PRIMARY KEY (id),
    CONSTRAINT ck_transactions_capture_has_content CHECK ((capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)),
    CONSTRAINT ck_transactions_telegram_ids_match_capture_kind CHECK ((capture_kind = 2 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL) OR (capture_kind <> 2 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL)),
    CONSTRAINT "FK_transactions_wallets_wallet_id" FOREIGN KEY (wallet_id) REFERENCES public.wallets (id) ON DELETE RESTRICT
);


CREATE TABLE public.balance_checks (
    transaction_id uuid NOT NULL,
    wallet_id uuid NOT NULL,
    computed_before numeric(19,4) NOT NULL,
    stated_amount numeric(19,4) NOT NULL,
    currency character varying(3) NOT NULL,
    CONSTRAINT "PK_balance_checks" PRIMARY KEY (transaction_id),
    CONSTRAINT "FK_balance_checks_transactions_transaction_id" FOREIGN KEY (transaction_id) REFERENCES public.transactions (id) ON DELETE CASCADE,
    CONSTRAINT "FK_balance_checks_wallets_wallet_id" FOREIGN KEY (wallet_id) REFERENCES public.wallets (id) ON DELETE RESTRICT
);


CREATE TABLE public.categorization_jobs (
    id uuid NOT NULL,
    transaction_id uuid NOT NULL,
    kind integer NOT NULL,
    instruction text,
    source_message_id integer,
    instruction_day date,
    voice_file_id text,
    status integer NOT NULL,
    attempt_count integer NOT NULL,
    run_after timestamptz NOT NULL,
    claimed_at timestamptz,
    claimed_by character varying(128),
    last_error text,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT "PK_categorization_jobs" PRIMARY KEY (id),
    CONSTRAINT ck_categorization_jobs_correction_has_instruction CHECK (kind <> 1 OR instruction IS NOT NULL),
    CONSTRAINT ck_categorization_jobs_transcription_has_voice_file CHECK (kind <> 3 OR voice_file_id IS NOT NULL),
    CONSTRAINT "FK_categorization_jobs_transactions_transaction_id" FOREIGN KEY (transaction_id) REFERENCES public.transactions (id) ON DELETE CASCADE
);


CREATE TABLE public.entries (
    id uuid NOT NULL,
    transaction_id uuid NOT NULL,
    wallet_id uuid NOT NULL,
    role integer NOT NULL,
    amount numeric(19,4) NOT NULL,
    currency character varying(3) NOT NULL,
    CONSTRAINT "PK_entries" PRIMARY KEY (id),
    CONSTRAINT "FK_entries_transactions_transaction_id" FOREIGN KEY (transaction_id) REFERENCES public.transactions (id) ON DELETE CASCADE,
    CONSTRAINT "FK_entries_wallets_wallet_id" FOREIGN KEY (wallet_id) REFERENCES public.wallets (id) ON DELETE RESTRICT
);


CREATE TABLE public.line_items (
    id uuid NOT NULL,
    transaction_id uuid NOT NULL,
    description character varying(512) NOT NULL,
    category_id uuid,
    categorized_by integer NOT NULL,
    merchant_id uuid,
    amount numeric(19,4) NOT NULL,
    currency character varying(3) NOT NULL,
    CONSTRAINT "PK_line_items" PRIMARY KEY (id),
    CONSTRAINT "FK_line_items_categories_category_id" FOREIGN KEY (category_id) REFERENCES public.categories (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_line_items_merchants_merchant_id" FOREIGN KEY (merchant_id) REFERENCES public.merchants (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_line_items_transactions_transaction_id" FOREIGN KEY (transaction_id) REFERENCES public.transactions (id) ON DELETE CASCADE
);


CREATE TABLE public.transaction_revisions (
    id uuid NOT NULL,
    transaction_id uuid NOT NULL,
    revision_number integer NOT NULL,
    kind integer NOT NULL,
    instruction text,
    status_before integer NOT NULL,
    status_after integer NOT NULL,
    snapshot jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT "PK_transaction_revisions" PRIMARY KEY (id),
    CONSTRAINT "FK_transaction_revisions_transactions_transaction_id" FOREIGN KEY (transaction_id) REFERENCES public.transactions (id) ON DELETE RESTRICT
);


CREATE INDEX "IX_balance_checks_wallet_id" ON public.balance_checks (wallet_id);


CREATE INDEX "IX_categories_parent_id" ON public.categories (parent_id);


CREATE UNIQUE INDEX "IX_categories_slug" ON public.categories (slug);


CREATE INDEX "IX_categorization_jobs_status_run_after" ON public.categorization_jobs (status, run_after);


CREATE UNIQUE INDEX "IX_categorization_jobs_transaction_id_source_message_id_kind" ON public.categorization_jobs (transaction_id, source_message_id, kind) WHERE source_message_id IS NOT NULL;


CREATE INDEX "IX_entries_transaction_id" ON public.entries (transaction_id);


CREATE INDEX ix_entries_wallet_id ON public.entries (wallet_id);


CREATE INDEX "IX_line_items_category_id" ON public.line_items (category_id);


CREATE INDEX "IX_line_items_merchant_id" ON public.line_items (merchant_id);


CREATE INDEX "IX_line_items_transaction_id" ON public.line_items (transaction_id);


CREATE INDEX "IX_merchant_aliases_merchant_id" ON public.merchant_aliases (merchant_id);


CREATE UNIQUE INDEX "IX_transaction_revisions_transaction_id_revision_number" ON public.transaction_revisions (transaction_id, revision_number);


CREATE UNIQUE INDEX "IX_transactions_telegram_chat_id_telegram_message_id" ON public.transactions (telegram_chat_id, telegram_message_id) WHERE telegram_chat_id IS NOT NULL;


CREATE INDEX "IX_transactions_wallet_id" ON public.transactions (wallet_id);


CREATE UNIQUE INDEX ix_wallets_one_default_per_currency ON public.wallets (currency) WHERE is_default_for_currency;


