package org.androidondesktop.smoke;

import android.app.Activity;
import android.os.Bundle;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Small, permission-free APK for checking installation, input and window launch. */
public final class MainActivity extends Activity {
    private int count;

    @Override
    public void onCreate(Bundle state) {
        super.onCreate(state);
        count = state == null ? 0 : state.getInt("count", 0);

        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        int padding = dp(24);
        layout.setPadding(padding, padding, padding, padding);

        TextView heading = new TextView(this);
        heading.setText("AndroidOnDesktop");
        heading.setTextSize(26);
        layout.addView(heading);

        EditText input = new EditText(this);
        input.setId(1001);
        input.setSingleLine(true);
        input.setTextSize(20);
        input.setHint("在此输入文字");
        layout.addView(input, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        final Button counter = new Button(this);
        counter.setTextSize(20);
        updateCount(counter);
        counter.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                count++;
                updateCount(counter);
            }
        });
        layout.addView(counter, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));
        setContentView(layout);
    }

    private void updateCount(Button button) {
        button.setText("点击计数：" + count);
    }

    @Override
    public void onSaveInstanceState(Bundle state) {
        state.putInt("count", count);
        super.onSaveInstanceState(state);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
