//! Handler traits for the callbacks the agent makes into your application. Closures implement them, so
//! `SessionConfig::default().with_permission_handler(|req: &PermissionRequest, _: &Invocation| ...)` works.

use crate::types::{
    ExitPlanModeResult, Invocation, PermissionDecision, PermissionRequest, UserQuestion,
    UserQuestionAnswer,
};

/// Decides whether a tool call may run. Without a handler sessions are deny-by-default.
pub trait PermissionHandler: Send + Sync {
    fn handle(&self, request: &PermissionRequest, invocation: &Invocation) -> PermissionDecision;
}

impl<F> PermissionHandler for F
where
    F: Fn(&PermissionRequest, &Invocation) -> PermissionDecision + Send + Sync,
{
    fn handle(&self, request: &PermissionRequest, invocation: &Invocation) -> PermissionDecision {
        self(request, invocation)
    }
}

/// Approves every permission request.
pub struct ApproveAllHandler;

impl PermissionHandler for ApproveAllHandler {
    fn handle(&self, _: &PermissionRequest, _: &Invocation) -> PermissionDecision {
        PermissionDecision::approve_once()
    }
}

/// Rejects every permission request (same as no handler).
pub struct DenyAllHandler;

impl PermissionHandler for DenyAllHandler {
    fn handle(&self, _: &PermissionRequest, _: &Invocation) -> PermissionDecision {
        PermissionDecision::reject(Some("Rejected by the SDK host.".into()))
    }
}

/// Answers the model's AskUserQuestion tool.
pub trait UserInputHandler: Send + Sync {
    fn handle(
        &self,
        questions: &[UserQuestion],
        invocation: &Invocation,
    ) -> Vec<UserQuestionAnswer>;
}

impl<F> UserInputHandler for F
where
    F: Fn(&[UserQuestion], &Invocation) -> Vec<UserQuestionAnswer> + Send + Sync,
{
    fn handle(
        &self,
        questions: &[UserQuestion],
        invocation: &Invocation,
    ) -> Vec<UserQuestionAnswer> {
        self(questions, invocation)
    }
}

/// Reviews the plan (markdown) when the agent leaves plan mode.
pub trait ExitPlanModeHandler: Send + Sync {
    fn handle(&self, plan: &str, invocation: &Invocation) -> ExitPlanModeResult;
}

impl<F> ExitPlanModeHandler for F
where
    F: Fn(&str, &Invocation) -> ExitPlanModeResult + Send + Sync,
{
    fn handle(&self, plan: &str, invocation: &Invocation) -> ExitPlanModeResult {
        self(plan, invocation)
    }
}
