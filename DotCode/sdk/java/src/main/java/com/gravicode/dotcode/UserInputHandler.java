package com.gravicode.dotcode;

import java.util.List;

/** Answers the model's AskUserQuestion tool. */
@FunctionalInterface
public interface UserInputHandler {
    List<UserQuestionAnswer> handle(List<UserQuestion> questions, Invocation invocation) throws Exception;
}
